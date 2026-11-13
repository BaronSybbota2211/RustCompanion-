using System;
namespace RustCompanion.Core.Watch;

/// <summary>Ссылка для ручного просмотра игрока на сайте BattleMetrics в браузере пользователя. Приложение ничего не загружает с сайта само.</summary>
public static class BattleMetricsLink
{
    public static string? ForSteamId(string? steamId) =>
        Net.SteamClient.IsValidSteamId64(steamId?.Trim())
            ? "https://www.battlemetrics.com/players?filter%5Bsearch%5D=" + steamId!.Trim()
            : null;

    /// <summary>Скрипт поиска игрока через API BattleMetrics (запрос идёт со страницы battlemetrics.com, с сессией пользователя). Результат — числовой ID игрока BattleMetrics или пустая строка.</summary>
    public static string? LookupScript(string? steamId)
    {
        var id = steamId?.Trim();
        if (!Net.SteamClient.IsValidSteamId64(id)) return null;
        return "fetch('https://api.battlemetrics.com/players?filter[search]=" + id + "&page[size]=1',{headers:{Accept:'application/json'},credentials:'include'})"
             + ".then(function(r){return r.ok?r.json():null;}).then(function(j){return j&&j.data&&j.data.length?String(j.data[0].id):'';}).catch(function(){return '';})";
    }

    public static string PlayerPage(string bmId) => "https://www.battlemetrics.com/players/" + Uri.EscapeDataString(bmId);
}

/// <summary>Страница входа BattleMetrics и признаки того, что вход завершён (по адресу страницы, без чтения паролей и cookies).</summary>
public static class BattleMetricsLogin
{
    public const string LoginUrl = "https://www.battlemetrics.com/account/login";

    /// <summary>True, если адрес — страница BattleMetrics, не относящаяся к процессу входа (то есть пользователь вошёл и сайт его перенаправил).</summary>
    public static bool IsLoggedInPage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (!u.Host.Equals("www.battlemetrics.com", StringComparison.OrdinalIgnoreCase) && !u.Host.Equals("battlemetrics.com", StringComparison.OrdinalIgnoreCase)) return false;
        var p = u.AbsolutePath.ToLowerInvariant();
        return !(p.StartsWith("/account/login") || p.StartsWith("/auth") || p.StartsWith("/account/register") || p.StartsWith("/login"));
    }
}

/// <summary>Статистика игрока на rustbans.ru (вход на сайте не нужен). Страница не принимает SteamID в адресе, поэтому ID подставляется в поле поиска скриптом.</summary>
public static class RustBansLink
{
    public const string Url = "https://rustbans.ru/rust-player-stats";

    /// <summary>JS, который вводит SteamID64 в поле поиска страницы и нажимает «Найти». Null, если ID некорректен (в скрипт попадают только 17 цифр).</summary>
    public static string? FillScript(string? steamId)
    {
        var id = steamId?.Trim();
        if (!Net.SteamClient.IsValidSteamId64(id)) return null;
        return "(function(){var id='" + id + "';var inputs=[].slice.call(document.querySelectorAll('input'));"
             + "var inp=inputs.find(function(i){return /7656119|steamcommunity/i.test(i.placeholder||'');})||inputs.find(function(i){return (i.type||'text')==='text'||i.type==='search';});"
             + "if(!inp)return 'no-input';var set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;set.call(inp,id);"
             + "inp.dispatchEvent(new Event('input',{bubbles:true}));inp.dispatchEvent(new Event('change',{bubbles:true}));"
             + "var btn=[].slice.call(document.querySelectorAll('button,input[type=submit]')).find(function(b){return /найти/i.test((b.innerText||b.value||''));});"
             + "if(btn){btn.click();return 'clicked';}if(inp.form){inp.form.requestSubmit?inp.form.requestSubmit():inp.form.submit();return 'submitted';}return 'no-button';})()";
    }
}
