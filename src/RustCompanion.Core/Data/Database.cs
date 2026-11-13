using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RustCompanion.Core.Data;

internal static class Sql
{
    public static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }
    public static string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset? ParseIso(object o) =>
        o is DBNull ? null : DateTimeOffset.Parse((string)o, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed class Database
{
    private static readonly string[] Migrations =
    {
        @"CREATE TABLE timers(id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, name TEXT NOT NULL,
            started_at TEXT NULL, interval_seconds INTEGER NOT NULL, lead_seconds INTEGER NOT NULL, note TEXT NOT NULL DEFAULT '',
            notify_enabled INTEGER NOT NULL DEFAULT 1, lead_notified INTEGER NOT NULL DEFAULT 0, due_notified INTEGER NOT NULL DEFAULT 0);
          CREATE TABLE timer_history(id INTEGER PRIMARY KEY AUTOINCREMENT, timer_id INTEGER NOT NULL REFERENCES timers(id) ON DELETE CASCADE,
            at TEXT NOT NULL, action TEXT NOT NULL, note TEXT NOT NULL DEFAULT '');
          CREATE TABLE servers(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, host TEXT NOT NULL, port INTEGER NOT NULL,
            favorite INTEGER NOT NULL DEFAULT 0, UNIQUE(host, port));
          CREATE TABLE server_observations(id INTEGER PRIMARY KEY AUTOINCREMENT, server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
            at TEXT NOT NULL, players INTEGER NOT NULL, max_players INTEGER NOT NULL);
          CREATE TABLE players(id INTEGER PRIMARY KEY AUTOINCREMENT, steam_id TEXT NOT NULL UNIQUE, label TEXT NOT NULL DEFAULT '');
          CREATE TABLE base_profiles(name TEXT PRIMARY KEY, json TEXT NOT NULL);
          CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);",
        // v2: мониторинг игроков (раньше внешний сервис), группы, история, дедупликация уведомлений
        @"ALTER TABLE servers ADD COLUMN bm_id TEXT NULL;
          CREATE TABLE groups(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, color TEXT NOT NULL DEFAULT '#D9653B',
            notify_join INTEGER NOT NULL DEFAULT 1, notify_leave INTEGER NOT NULL DEFAULT 1, threshold INTEGER NOT NULL DEFAULT 0);
          CREATE TABLE watch_entries(id INTEGER PRIMARY KEY AUTOINCREMENT, server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
            bm_player_id TEXT NOT NULL, steam_id TEXT NULL, name TEXT NOT NULL, group_id INTEGER NULL REFERENCES groups(id) ON DELETE SET NULL,
            color TEXT NULL, notify_join INTEGER NOT NULL DEFAULT 1, notify_leave INTEGER NOT NULL DEFAULT 1, UNIQUE(server_id, bm_player_id));
          CREATE TABLE monitor_state(watch_id INTEGER PRIMARY KEY REFERENCES watch_entries(id) ON DELETE CASCADE,
            status INTEGER NOT NULL, absence_count INTEGER NOT NULL DEFAULT 0, last_present TEXT NULL, last_checked TEXT NULL,
            last_counted_source_ts TEXT NULL, exit_notified INTEGER NOT NULL DEFAULT 0, episode INTEGER NOT NULL DEFAULT 0, last_error TEXT NULL);
          CREATE TABLE activity_events(id INTEGER PRIMARY KEY AUTOINCREMENT, watch_id INTEGER NOT NULL REFERENCES watch_entries(id) ON DELETE CASCADE,
            at TEXT NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '');
          CREATE TABLE notified_events(event_key TEXT PRIMARY KEY, at TEXT NOT NULL);
          CREATE TABLE group_state(group_id INTEGER NOT NULL REFERENCES groups(id) ON DELETE CASCADE, server_id INTEGER NOT NULL,
            above INTEGER NOT NULL DEFAULT 0, episode INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(group_id, server_id));",
        // v3: ручные наблюдения событий, настройки серверов, контейнер, гниение, настройки уведомлений (по умолчанию всё выключено)
        @"CREATE TABLE observed_events(id INTEGER PRIMARY KEY AUTOINCREMENT, server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
            kind INTEGER NOT NULL, at TEXT NOT NULL, note TEXT NOT NULL DEFAULT '');
          CREATE INDEX ix_observed_events ON observed_events(server_id, kind, at);
          CREATE TABLE server_prefs(server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE, key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(server_id, key));
          CREATE TABLE container_timers(id INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT NOT NULL, server_id INTEGER NULL REFERENCES servers(id) ON DELETE SET NULL, started_at TEXT NOT NULL);
          CREATE TABLE decay_objects(id INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT NOT NULL, element TEXT NOT NULL, material INTEGER NOT NULL, grid TEXT NOT NULL,
            noticed_at TEXT NOT NULL, hp REAL NULL, max_hp REAL NULL, notify INTEGER NOT NULL DEFAULT 0, destroyed INTEGER NOT NULL DEFAULT 0, version INTEGER NOT NULL DEFAULT 0);
          CREATE TABLE notification_prefs(event_id TEXT PRIMARY KEY, overlay INTEGER NOT NULL DEFAULT 0, system INTEGER NOT NULL DEFAULT 0, external INTEGER NOT NULL DEFAULT 0);",
        // v4: отслеживание конкретных РТ и универсальный счётчик респавна (нажатия «РТ респавн»; сброс не удаляет историю)
        @"CREATE TABLE monument_tracking(server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE, monument_id TEXT NOT NULL, added_at TEXT NOT NULL,
            PRIMARY KEY(server_id, monument_id));
          CREATE TABLE monument_respawns(id INTEGER PRIMARY KEY AUTOINCREMENT, server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
            monument_id TEXT NOT NULL, at TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1);
          CREATE INDEX ix_monument_respawns ON monument_respawns(server_id, monument_id, active, at);",
        // v5: недавно выбранные серверы и назначения глобальных горячих клавиш
        @"CREATE TABLE recent_servers(bm_id TEXT PRIMARY KEY, name TEXT NOT NULL, ip TEXT NULL, port INTEGER NULL, map TEXT NULL, country TEXT NULL, selected_at TEXT NOT NULL);
          CREATE TABLE hotkeys(event_key TEXT PRIMARY KEY, modifiers INTEGER NOT NULL, vk INTEGER NOT NULL);",
        // v6: Rust+ — сопряжённые серверы (токен игрока хранится зашифрованно вне БД), устройства и их события
        @"CREATE TABLE rp_servers(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, ip TEXT NOT NULL, port INTEGER NOT NULL, player_id TEXT NOT NULL,
            paired_at TEXT NOT NULL, last_connected TEXT NULL, UNIQUE(ip, port, player_id));
          CREATE TABLE rp_devices(id INTEGER PRIMARY KEY AUTOINCREMENT, server_id INTEGER NOT NULL REFERENCES rp_servers(id) ON DELETE CASCADE,
            entity_id TEXT NOT NULL, kind INTEGER NOT NULL, name TEXT NOT NULL, grp TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '',
            alarm_notify INTEGER NOT NULL DEFAULT 0, UNIQUE(server_id, entity_id));
          CREATE TABLE rp_device_events(id INTEGER PRIMARY KEY AUTOINCREMENT, device_id INTEGER NOT NULL REFERENCES rp_devices(id) ON DELETE CASCADE,
            at TEXT NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '');",
        // v7: переход на Steam Query (A2S). Внешние ID прежнего источника заменены ключами: сервер — «хост:порт запросов», игрок — ник.
        // Локальная история наблюдений: расчётное (по опросам) время присутствия, не точное игровое время.
        @"UPDATE servers SET bm_id = host || ':' || port WHERE bm_id IS NOT NULL;
          UPDATE OR IGNORE watch_entries SET bm_player_id = name;
          DELETE FROM recent_servers;
          ALTER TABLE servers RENAME COLUMN bm_id TO source_key;
          ALTER TABLE recent_servers RENAME COLUMN bm_id TO source_key;
          ALTER TABLE watch_entries RENAME COLUMN bm_player_id TO player_key;
          CREATE TABLE presence_history(id INTEGER PRIMARY KEY AUTOINCREMENT, server_key TEXT NOT NULL, name TEXT NOT NULL,
            first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, samples INTEGER NOT NULL DEFAULT 1, est_seconds REAL NOT NULL DEFAULT 0, reported_seconds REAL NULL);
          CREATE INDEX ix_presence_history ON presence_history(server_key, name, last_seen);
          CREATE INDEX ix_presence_history_seen ON presence_history(server_key, last_seen);",
        // v8 (в fixed11 — v7; перенесена после A2S-v7, который переименовывает колонки): независимые Team Chat-команды устройств; секреты и токены здесь не хранятся
        @"CREATE TABLE IF NOT EXISTS rp_device_commands(device_id INTEGER PRIMARY KEY REFERENCES rp_devices(id) ON DELETE CASCADE,
            enabled INTEGER NOT NULL DEFAULT 0, mode INTEGER NOT NULL DEFAULT 0, prefix TEXT NOT NULL DEFAULT '!',
            toggle_command TEXT NOT NULL DEFAULT '', on_command TEXT NOT NULL DEFAULT '', off_command TEXT NOT NULL DEFAULT '',
            allowed_senders TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS rp_device_module_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS rp_device_processed_messages(server_id INTEGER NOT NULL REFERENCES rp_servers(id) ON DELETE CASCADE,
            message_key TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(server_id,message_key));
          CREATE TABLE IF NOT EXISTS rp_device_command_history(id INTEGER PRIMARY KEY AUTOINCREMENT,
            server_id INTEGER NOT NULL REFERENCES rp_servers(id) ON DELETE CASCADE,
            device_id INTEGER NULL REFERENCES rp_devices(id) ON DELETE SET NULL,
            message_key TEXT NOT NULL, sender TEXT NOT NULL, command TEXT NOT NULL, target_state INTEGER NULL,
            status TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '', at TEXT NOT NULL);
          CREATE INDEX IF NOT EXISTS ix_rp_device_command_history ON rp_device_command_history(server_id,id);",
        // v9: уровни достоверности личности игрока, история ников по SteamID64 и независимая база опрашиваемых серверов
        @"ALTER TABLE watch_entries ADD COLUMN identity_level INTEGER NOT NULL DEFAULT 0;
          CREATE TABLE player_names(steam_id TEXT NOT NULL, name TEXT NOT NULL, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, PRIMARY KEY(steam_id, name));
          CREATE TABLE known_servers(server_key TEXT PRIMARY KEY, name TEXT NOT NULL, host TEXT NOT NULL, port INTEGER NOT NULL,
            poll_enabled INTEGER NOT NULL DEFAULT 1, added_at TEXT NOT NULL, last_polled TEXT NULL, last_players INTEGER NULL);
          CREATE INDEX ix_known_servers_poll ON known_servers(poll_enabled, last_polled);",
        // v10: устойчивая привязка Rust+ по Server ID, история достоверной синхронизации и карточки обнаруженного сопряжения.
        // Уведомление OnEntityPairing означает завершённое в игре сопряжение; отдельной команды accept/reject в протоколе нет.
        @"ALTER TABLE rp_servers ADD COLUMN rustplus_id TEXT NULL;
          ALTER TABLE rp_devices ADD COLUMN added_at TEXT NOT NULL DEFAULT '';
          ALTER TABLE rp_devices ADD COLUMN last_sync_at TEXT NULL;
          ALTER TABLE rp_devices ADD COLUMN last_is_on INTEGER NULL;
          CREATE TABLE rp_device_pairing_requests(id INTEGER PRIMARY KEY AUTOINCREMENT,
            server_id INTEGER NOT NULL REFERENCES rp_servers(id) ON DELETE CASCADE,
            server_uid TEXT NOT NULL DEFAULT '', player_id TEXT NOT NULL DEFAULT '', persistent_id TEXT NULL,
            request_key TEXT NOT NULL UNIQUE, entity_id TEXT NOT NULL, kind INTEGER NOT NULL, name TEXT NOT NULL,
            status INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, last_error TEXT NOT NULL DEFAULT '');
          CREATE INDEX ix_rp_pairing_pending ON rp_device_pairing_requests(server_id,status,entity_id);",
        // v11: собственный текст уведомления для каждой Smart Alarm (пусто — стандартный)
        @"ALTER TABLE rp_devices ADD COLUMN alarm_text TEXT NOT NULL DEFAULT '';",
        // v12: канал уведомления при срабатывании для каждой сигнализации (0 — выкл, 1 — Team Chat, 2 — Overlay, 3 — оба)
        @"ALTER TABLE rp_devices ADD COLUMN alarm_channel INTEGER NOT NULL DEFAULT 2;",
        // v13: зарезервирована; собственные метки на карте не используются (таблица, если была создана промежуточной сборкой, удаляется)
        @"DROP TABLE IF EXISTS rp_user_markers;"
    };

    private readonly string _path;
    public Database(string path) { _path = path; }

    public string ConnectionString => new SqliteConnectionStringBuilder
    { DataSource = _path, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA foreign_keys=ON;";
        p.ExecuteNonQuery();
        return c;
    }

    public Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default) =>
        Task.Run(() => { using var c = Open(); return work(c); }, ct);

    public Task RunAsync(Action<SqliteConnection> work, CancellationToken ct = default) =>
        Task.Run(() => { using var c = Open(); work(c); }, ct);

    /// <summary>Резервная копия базы через SQLite Backup API (безопасно при открытой базе).</summary>
    public void BackupTo(string destinationPath)
    {
        using var src = Open();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
        dst.Open();
        src.BackupDatabase(dst);
    }

    /// <summary>Применяет миграции. Возвращает true, если повреждённая база была отложена и создана заново.</summary>
    public bool Initialize()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try { Migrate(); RepairSchema(); return false; }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26 && File.Exists(_path))
        {
            File.Move(_path, $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}");
            Migrate();
            RepairSchema();
            return true;
        }
    }

    private void Migrate()
    {
        long version;
        using (var c = Open())
        {
            using var q = c.CreateCommand();
            q.CommandText = "PRAGMA user_version;";
            version = (long)(q.ExecuteScalar() ?? 0L);
        }
        if (version > Migrations.Length)
            throw new InvalidOperationException("База создана более новой версией приложения.");
        if (version == Migrations.Length) return;
        if (version > 0) File.Copy(_path, $"{_path}.bak-v{version}", overwrite: true); // резервная копия перед миграцией

        using var conn = Open();
        for (var i = (int)version; i < Migrations.Length; i++)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[i] + $"PRAGMA user_version={i + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    // ---- Самовосстановление схемы ----------------------------------------------------------------
    // База могла быть создана другой сборкой приложения с иным составом миграций: номер версии (PRAGMA user_version) совпадает,
    // а таблицы/столбцы нет — тогда запросы падают с «no such column». После миграций структура сверяется с ожидаемой из списка миграций:
    // недостающие таблицы создаются, недостающие столбцы добавляются, несовместимые таблицы пересоздаются (старая сохраняется как <имя>_old_<время>).

    private sealed class TableSpec
    {
        public string Name = ""; public List<(string Col, string Def)> Columns = new(); public List<string> Constraints = new();
        /// <summary>Собирается из столбцов и ограничений: столбец, добавленный позже, должен стоять ДО табличных ограничений (иначе SQLite отклонит запрос).</summary>
        public string CreateSql => $"CREATE TABLE {Name}({string.Join(", ", Columns.Select(x => x.Def).Concat(Constraints))})";
    }

    private static readonly Lazy<(List<TableSpec> Tables, List<string> Indexes)> Expected = new(BuildExpected);
    private static readonly List<(string Table, string From, string To)> LegacyRenames = new();

    private static List<string> SplitTop(string body)
    {
        var parts = new List<string>(); var depth = 0; var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '(') depth++;
            else if (body[i] == ')') depth--;
            else if (body[i] == ',' && depth == 0) { parts.Add(body[start..i]); start = i + 1; }
        }
        parts.Add(body[start..]);
        return parts;
    }

    private static (List<TableSpec>, List<string>) BuildExpected()
    {
        var tables = new List<TableSpec>(); var indexes = new List<string>();
        var byName = new Dictionary<string, TableSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var stmt in Migrations.SelectMany(m => m.Split(';')).Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            var ct = Regex.Match(stmt, @"^CREATE TABLE\s+(?:IF NOT EXISTS\s+)?(\w+)\s*\(", RegexOptions.IgnoreCase);
            if (ct.Success)
            {
                var open = stmt.IndexOf('('); var close = stmt.LastIndexOf(')');
                var spec = new TableSpec { Name = ct.Groups[1].Value };
                foreach (var raw in SplitTop(stmt.Substring(open + 1, close - open - 1)))
                {
                    var part = raw.Trim();
                    if (part.Length == 0) continue;
                    if (Regex.IsMatch(part, @"^(UNIQUE|PRIMARY|FOREIGN|CHECK|CONSTRAINT)\b", RegexOptions.IgnoreCase)) { spec.Constraints.Add(part); continue; }
                    spec.Columns.Add((part.Split(' ', '\t', '\r', '\n')[0], part));
                }
                tables.Add(spec); byName[spec.Name] = spec; continue;
            }
            var rn = Regex.Match(stmt, @"^ALTER TABLE\s+(\w+)\s+RENAME COLUMN\s+(\w+)\s+TO\s+(\w+)\s*$", RegexOptions.IgnoreCase);
            if (rn.Success && byName.TryGetValue(rn.Groups[1].Value, out var rt))
            {
                // переименование столбца (A2S-v7: bm_id → source_key): ожидаемая схема следует за ним, иначе ремонт вернул бы устаревший столбец
                var fromCol = rn.Groups[2].Value; var toCol = rn.Groups[3].Value;
                lock (LegacyRenames) LegacyRenames.Add((rn.Groups[1].Value, fromCol, toCol));
                for (var ci = 0; ci < rt.Columns.Count; ci++)
                    if (string.Equals(rt.Columns[ci].Col, fromCol, StringComparison.OrdinalIgnoreCase))
                        rt.Columns[ci] = (toCol, Regex.Replace(rt.Columns[ci].Def, @"^\s*" + Regex.Escape(fromCol) + @"\b", toCol));
                for (var i = 0; i < rt.Constraints.Count; i++)
                    rt.Constraints[i] = Regex.Replace(rt.Constraints[i], @"\b" + Regex.Escape(fromCol) + @"\b", toCol);
                continue;
            }
            var at = Regex.Match(stmt, @"^ALTER TABLE\s+(\w+)\s+ADD COLUMN\s+(\w+)\s+(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (at.Success && byName.TryGetValue(at.Groups[1].Value, out var t))
            {
                var def = at.Groups[2].Value + " " + at.Groups[3].Value.Trim();
                t.Columns.Add((at.Groups[2].Value, def));
                continue;      // в пересоздаваемой таблице столбец, добавленный позже, уже есть (CreateSql собирается из списка столбцов)
            }
            if (Regex.IsMatch(stmt, @"^CREATE INDEX\b", RegexOptions.IgnoreCase)) indexes.Add(stmt);
        }
        return (tables, indexes);
    }

    private static bool CanAddColumn(string def)
    {
        var u = def.ToUpperInvariant();
        if (u.Contains("PRIMARY KEY") || u.Contains("UNIQUE")) return false;
        return !u.Contains("NOT NULL") || u.Contains("DEFAULT");
    }

    private static bool HasColumn(SqliteConnection c, string table, string column)
    {
        using var q = c.CreateCommand();
        q.CommandText = $"PRAGMA table_info({table});";
        using var r = q.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// База с user_version 7, созданная другой сборкой (fixed11 с BattleMetrics), не прошла переименование столбцов A2S-v7.
    /// Переименовываем их на месте, сохраняя данные пользователя: bm_id → source_key (значение пересчитывается в «хост:порт»),
    /// bm_player_id → player_key (ключ игрока — ник), старые «недавние серверы» BattleMetrics непригодны и очищаются.
    /// </summary>
    private static void ApplyLegacyRenames(SqliteConnection c)
    {
        _ = Expected.Value;   // гарантирует, что список переименований заполнен
        (string Table, string From, string To)[] list;
        lock (LegacyRenames) list = LegacyRenames.ToArray();
        foreach (var (table, fromCol, toCol) in list)
        {
            try
            {
                if (!HasColumn(c, table, fromCol) || HasColumn(c, table, toCol)) continue;
                Exec(c, $"ALTER TABLE {table} RENAME COLUMN {fromCol} TO {toCol}");
                if (table == "servers" && toCol == "source_key") Exec(c, "UPDATE servers SET source_key = host || ':' || port WHERE source_key IS NOT NULL AND source_key NOT LIKE '%:%'");
                else if (table == "watch_entries" && toCol == "player_key") Exec(c, "UPDATE OR IGNORE watch_entries SET player_key = name");
                else if (table == "recent_servers") Exec(c, "DELETE FROM recent_servers");
            }
            catch (SqliteException) { /* не удалось — общий ремонт ниже сохранит данные в резервной таблице */ }
        }
    }

    private static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private void RepairSchema()
    {
        try
        {
            using var c = Open();
            var (tables, indexes) = Expected.Value;
            ApplyLegacyRenames(c);
            foreach (var t in tables)
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var q = c.CreateCommand())
                {
                    q.CommandText = $"PRAGMA table_info({t.Name});";
                    using var r = q.ExecuteReader();
                    while (r.Read()) existing.Add(r.GetString(1));
                }
                if (existing.Count == 0) { Exec(c, t.CreateSql); continue; }
                var missing = t.Columns.Where(x => !existing.Contains(x.Col)).ToList();
                if (missing.Count == 0) continue;
                if (missing.All(m => CanAddColumn(m.Def)))
                {
                    foreach (var m in missing) Exec(c, $"ALTER TABLE {t.Name} ADD COLUMN {m.Def}");
                    continue;
                }
                // Столбец нельзя добавить (ключ/NOT NULL без значения по умолчанию): пересоздаём таблицу, общие столбцы переносим, старую оставляем как резервную.
                var old = $"{t.Name}_old_{DateTime.UtcNow:yyyyMMddHHmmss}";
                Exec(c, "PRAGMA foreign_keys=OFF;"); Exec(c, "PRAGMA legacy_alter_table=ON;");
                try
                {
                    Exec(c, $"ALTER TABLE {t.Name} RENAME TO {old}");
                    Exec(c, t.CreateSql);
                    var common = t.Columns.Select(x => x.Col).Where(x => existing.Contains(x)).ToList();
                    if (common.Count > 0)
                    {
                        try { var cols = string.Join(",", common); Exec(c, $"INSERT INTO {t.Name}({cols}) SELECT {cols} FROM {old}"); }
                        catch (SqliteException) { /* данные не совместимы — остаются в резервной таблице */ }
                    }
                }
                finally { Exec(c, "PRAGMA legacy_alter_table=OFF;"); Exec(c, "PRAGMA foreign_keys=ON;"); }
            }
            foreach (var ix in indexes) { try { Exec(c, ix); } catch (SqliteException) { /* индекс уже есть */ } }
        }
        catch (SqliteException) { /* исправление не удалось — приложение сообщит исходную ошибку при первом обращении */ }
    }
}
