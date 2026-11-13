using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RustCompanion.RustPlusAdapter;

/// <summary>
/// Вся работа с библиотекой RustPlusApi идёт через отражение: имена методов и событий предварительной версии 2 в документации раскрыты не полностью,
/// поэтому код не зависит от них на этапе компиляции, а несовпадения диагностируются во время работы (список доступных методов выводится в сообщении).
/// </summary>
internal static class Reflect
{
    public static Type? FindType(string name)
    {
        foreach (var asmName in new[] { "RustPlusApi", "RustPlusApi.Fcm", "RustPlusApi.Fcm.Registration" })
        {
            try
            {
                var asm = Assembly.Load(asmName);
                var t = asm.GetTypes().FirstOrDefault(x => x.Name == name);
                if (t is not null) return t;
            }
            catch (Exception) { /* сборка не найдена — пробуем следующую */ }
        }
        return null;
    }

    public static object? P(object? o, params string[] names)
    {
        if (o is null) return null;
        var t = o.GetType();
        foreach (var n in names)
        {
            var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p is not null && p.GetIndexParameters().Length == 0) { try { return p.GetValue(o); } catch (Exception) { } }
        }
        return null;
    }


    /// <summary>
    /// Создание экземпляра типа библиотеки, конструктор которого заранее неизвестен: перебираются все конструкторы (в т.ч. непубличные),
    /// параметры подбираются из переданных аргументов, значений по умолчанию, HttpClient, «пустых» логгеров и конструкторов без параметров.
    /// Если подходящего конструктора нет — исключение со списком реальных конструкторов и методов (чтобы можно было сопоставить API).
    /// </summary>
    /// <summary>Имена bool-параметров конструктора (подстроки, без регистра), которые нужно включить (например «proxy»). Выставляется на время одного вызова.</summary>
    public static string[] TrueBoolHints = Array.Empty<string>();
    /// <summary>Был ли bool-параметр из TrueBoolHints реально найден и включён при последнем создании.</summary>
    public static bool HintApplied;

    public static object? CreateInstance(Type t, params object?[] args)
    {
        if (t.IsAbstract && t.IsSealed) return null;      // статический класс — методы вызываются без экземпляра
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var ctors = t.GetConstructors(all).OrderBy(c => c.IsPublic ? 0 : 1).ThenBy(c => c.GetParameters().Length).ToList();
        foreach (var c in ctors)
        {
            var ps = c.GetParameters(); var values = new object?[ps.Length]; var used = new bool[args.Length]; var ok = true; var options = false;
            for (var i = 0; i < ps.Length && ok; i++)
            {
                var pt = ps[i].ParameterType; var idx = -1;
                for (var j = 0; j < args.Length && idx < 0; j++) if (!used[j] && args[j] is { } a && pt.IsInstanceOfType(a)) idx = j;
                if (idx >= 0) { used[idx] = true; values[i] = args[idx]; continue; }
                var npt = Nullable.GetUnderlyingType(pt) ?? pt;      // числа разных типов (int/long/ulong) подгоняем друг к другу
                for (var j = 0; j < args.Length && idx < 0; j++) if (!used[j] && args[j] is { } a && IsNumeric(npt) && IsNumeric(a.GetType())) idx = j;
                if (idx >= 0)
                {
                    try { values[i] = Convert.ChangeType(args[idx], npt); used[idx] = true; continue; }
                    catch (Exception) { idx = -1; }
                }
                if (pt == typeof(bool) && TrueBoolHints.Length > 0 && ps[i].Name is { } bn && TrueBoolHints.Any(h => bn.Contains(h, StringComparison.OrdinalIgnoreCase))) { values[i] = true; HintApplied = true; continue; }
                if (ps[i].HasDefaultValue) { values[i] = ps[i].DefaultValue is DBNull ? null : ps[i].DefaultValue; continue; }
                var auto = AutoValue(pt, 0, args);
                if (auto.Ok) { values[i] = auto.Value; if (args.Length > 0 && auto.Value is not null && pt.IsClass && pt != typeof(HttpClient) && !pt.Name.StartsWith("ILogger", StringComparison.Ordinal) && pt.GetConstructor(Type.EmptyTypes) is not null) options = true; } else ok = false;
            }
            if (!ok || !(options || used.All(u => u))) continue;
            try { return c.Invoke(values); }
            catch (TargetInvocationException) { throw; }
            catch (Exception) { /* пробуем следующий конструктор */ }
        }
        // Статическая фабрика вида Create()/CreateAsync() или свойство Instance — на случай, если публичных конструкторов нет.
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => t.IsAssignableFrom(m.ReturnType) && m.GetParameters().All(p => p.HasDefaultValue) && m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase)))
        { try { return m.Invoke(null, m.GetParameters().Select(p => p.DefaultValue is DBNull ? null : p.DefaultValue).ToArray()); } catch (Exception) { } }
        var sig = ctors.Count == 0 ? "конструкторов нет" : string.Join("; ", ctors.Select(c => (c.IsPublic ? "public " : "non-public ") + t.Name + "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")"));
        throw new InvalidOperationException($"Не удалось создать {t.FullName}: {sig}. Методы: {string.Join(", ", MethodNames(t))}.");
    }

    private static (bool Ok, object? Value) AutoValue(Type pt, int depth, object?[]? hints = null)
    {
        if (depth > 2) return (false, null);
        if (pt == typeof(HttpClient)) return (true, new HttpClient());
        if (pt == typeof(CancellationToken)) return (true, CancellationToken.None);
        if (pt == typeof(string)) return (false, null);
        if (pt.Name.StartsWith("ILogger", StringComparison.Ordinal) || pt.Name.StartsWith("ILoggerFactory", StringComparison.Ordinal))
        {
            try
            {
                var asm = "Microsoft.Extensions.Logging.Abstractions";
                if (pt.Name == "ILoggerFactory") return (true, Type.GetType($"Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory, {asm}")?.GetField("Instance")?.GetValue(null));
                if (pt.IsGenericType)
                {
                    var nl = Type.GetType($"Microsoft.Extensions.Logging.Abstractions.NullLogger`1, {asm}")?.MakeGenericType(pt.GetGenericArguments());
                    return (true, nl?.GetField("Instance")?.GetValue(null));
                }
                return (true, Type.GetType($"Microsoft.Extensions.Logging.Abstractions.NullLogger, {asm}")?.GetField("Instance")?.GetValue(null));
            }
            catch (Exception) { return (true, null); }
        }
        if (!pt.IsAbstract && !pt.IsInterface && pt.IsClass)
        {
            var d = pt.GetConstructor(Type.EmptyTypes);
            if (d is not null) { try { var o = d.Invoke(null); if (hints is { Length: > 0 }) Populate(o, hints); return (true, o); } catch (Exception) { } }
        }
        if (pt.IsValueType) return (true, Activator.CreateInstance(pt));
        return (true, null);      // последняя попытка: необязательная зависимость
    }


    /// <summary>Заполняет объект параметров по именам свойств (Ip/Host/Address, Port, PlayerId/SteamId, Token) из переданных аргументов: строка, числа по порядку.</summary>
    private static void Populate(object o, object?[] hints)
    {
        var str = hints.OfType<string>().FirstOrDefault();
        var nums = hints.Where(h => h is not null && h is not string && IsNumeric(h.GetType())).ToList();
        var ulongs = nums.Where(n => n is ulong).ToList(); var others = nums.Where(n => n is not ulong).ToList();
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            var pn = p.Name.ToLowerInvariant(); object? v = null;
            if (p.PropertyType == typeof(string) && (pn.Contains("ip") || pn.Contains("host") || pn.Contains("address") || pn == "server")) v = str;
            else if (pn.Contains("port")) v = others.FirstOrDefault();
            else if (pn.Contains("playerid") || pn.Contains("steamid")) v = ulongs.FirstOrDefault() ?? others.FirstOrDefault();
            else if (pn.Contains("token")) v = others.Skip(1).FirstOrDefault() ?? others.LastOrDefault();
            if (v is null) continue;
            try { p.SetValue(o, Convert.ChangeType(v, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)); } catch (Exception) { }
        }
    }

    public static string? S(object? v) => v?.ToString();
    public static int? I(object? v) { try { return v is null ? null : Convert.ToInt32(v); } catch (Exception) { return null; } }
    public static long? L(object? v) { try { return v is null ? null : Convert.ToInt64(v); } catch (Exception) { return null; } }
    public static double? D(object? v) { try { return v is null ? null : Convert.ToDouble(v); } catch (Exception) { return null; } }
    public static bool? B(object? v) => v is bool b ? b : null;
    public static ulong? U(object? v) { try { return v is null ? null : Convert.ToUInt64(v); } catch (Exception) { return null; } }
    public static DateTimeOffset? T(object? v) => v switch
    {
        DateTimeOffset d => d, DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
        long l when l > 1_000_000_000 => DateTimeOffset.FromUnixTimeSeconds(l), _ => null
    };

    public static IEnumerable<object> Items(object? v) => v is System.Collections.IEnumerable e && v is not string ? e.Cast<object>() : Enumerable.Empty<object>();

    public static IEnumerable<string> MethodNames(Type t, params string[] contains) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).Distinct()
            .Where(n => contains.Length == 0 || contains.Any(c => n.Contains(c, StringComparison.OrdinalIgnoreCase))).OrderBy(n => n);

    /// <summary>Вызов метода по имени. Параметры сопоставляются по типу (порядок не важен); CancellationToken подставляется автоматически, необязательные — по умолчанию.</summary>
    public static async Task<(bool Found, object? Result)> InvokeAsync(object? instance, Type type, string name, CancellationToken ct, params object?[] available)
    {
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            if (instance is null && !m.IsStatic) continue;
            var args = Bind(m.GetParameters(), ct, available);
            if (args is null) continue;
            var ret = m.Invoke(m.IsStatic ? null : instance, args);
            return (true, await Unwrap(ret));
        }
        return (false, null);
    }

    private static object?[]? Bind(ParameterInfo[] ps, CancellationToken ct, object?[] available)
    {
        var used = new bool[available.Length]; var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var pt = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
            if (pt == typeof(CancellationToken)) { args[i] = ct; continue; }
            var idx = -1;
            for (var j = 0; j < available.Length && idx < 0; j++)
                if (!used[j] && available[j] is { } a && (pt.IsInstanceOfType(a) || (IsNumeric(pt) && IsNumeric(a.GetType())))) idx = j;
            if (idx >= 0)
            {
                used[idx] = true;
                try { args[i] = pt.IsInstanceOfType(available[idx]) ? available[idx] : Convert.ChangeType(available[idx], pt); } catch (Exception) { return null; }
            }
            else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue is DBNull ? null : ps[i].DefaultValue;
            else return null;
        }
        return used.All(u => u) ? args : null;       // все переданные аргументы должны быть использованы
    }

    private static bool IsNumeric(Type t) => t.IsPrimitive && t != typeof(bool) && t != typeof(char);

    public static async Task<object?> Unwrap(object? ret)
    {
        if (ret is Task t)
        {
            await t.ConfigureAwait(false);
            var tt = t.GetType();
            if (!tt.IsGenericType) return null;
            var res = tt.GetProperty("Result")?.GetValue(t);
            return res?.GetType().Name == "VoidTaskResult" ? null : res;
        }
        if (ret is ValueTask vt) { await vt.ConfigureAwait(false); return null; }
        var rt = ret?.GetType();
        if (rt is not null && rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var task = rt.GetMethod("AsTask")!.Invoke(ret, null);
            return await Unwrap(task);
        }
        return ret;
    }

    /// <summary>Подписка на событие без знания типа аргументов: callback получает (sender, args).</summary>
    public static bool Subscribe(object target, EventInfo ev, Action<object?, object?> callback)
    {
        try
        {
            var handlerType = ev.EventHandlerType!;
            var invoke = handlerType.GetMethod("Invoke")!;
            var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            Expression a0 = ps.Length > 1 ? Expression.Convert(ps[0], typeof(object)) : Expression.Constant(null, typeof(object));
            Expression a1 = ps.Length > 1 ? Expression.Convert(ps[1], typeof(object)) : ps.Length == 1 ? Expression.Convert(ps[0], typeof(object)) : Expression.Constant(null, typeof(object));
            var body = Expression.Invoke(Expression.Constant(callback), a0, a1);
            var del = Expression.Lambda(handlerType, body, ps).Compile();
            ev.AddEventHandler(target, del);
            return true;
        }
        catch (Exception) { return false; }
    }

    public static (bool Ok, string? Code, string? Message, object? Data) Response(object? resp)
    {
        if (resp is null) return (true, null, null, null);
        if (resp is bool b) return (b, b ? null : "failed", b ? null : "операция вернула false", null);
        var ok = B(P(resp, "IsSuccess", "Success", "Ok")) ?? (P(resp, "Error") is null);
        var err = P(resp, "Error");
        return (ok, S(P(err, "Code", "Type", "Name")), S(P(err, "Message", "Description")) ?? (err?.ToString()), P(resp, "Data", "Value", "Result"));
    }
}
