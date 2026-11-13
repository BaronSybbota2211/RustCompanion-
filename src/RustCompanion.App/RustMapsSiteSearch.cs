using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RustCompanion.App;

/// <summary>
/// Пользуется собственным поиском серверов на rustmaps.com («Server Search»: название или IP) во встроенном браузере: открывает поиск, вводит запрос,
/// выбирает найденный сервер и возвращает адрес страницы его карты. У поиска нет открытого API, поэтому он управляется как страница
/// (по тексту пункта меню и полю «Server name or ip»); если разметка сайта изменится, отчёт покажет, на каком шаге остановились.
/// </summary>
internal sealed class RustMapsSiteSearch
{
    private readonly WebView2 _web;
    public RustMapsSiteSearch(WebView2 web) { _web = web; }

    private const string OpenJs = @"(function(){
 var norm=function(s){return (s||'').replace(/\s+/g,' ').trim();};
 var c=[].slice.call(document.querySelectorAll('a,button,li,div,span,p')).filter(function(e){return /^server search$/i.test(norm(e.textContent));});
 if(!c.length) return 'no-menu';
 c.sort(function(a,b){return a.querySelectorAll('*').length-b.querySelectorAll('*').length;});
 c[0].click(); return 'ok';})()";

    private const string FindMenuJs = @"(function(){
 var norm=function(s){return (s||'').replace(/\s+/g,' ').trim();}; var out=[];
 [].slice.call(document.querySelectorAll('a,button,li,div,span,p')).forEach(function(e){
   if(!/^server search$/i.test(norm(e.textContent))) return;
   var r=e.getBoundingClientRect(); if(r.width<2||r.height<2) return;
   out.push({x:r.left+r.width/2,y:r.top+r.height/2,a:r.width*r.height});
 }); out.sort(function(a,b){return a.a-b.a;}); return JSON.stringify(out.slice(0,6)); })()";

    private const string FindBurgerJs = @"(function(){
 var out=[]; [].slice.call(document.querySelectorAll('[class*=burger i],[class*=hamburger i],[class*=toggle i],[class*=menu-btn i],[aria-label*=menu i],button[class*=menu i]')).forEach(function(e){
   var r=e.getBoundingClientRect(); if(r.width<8||r.height<8) return; out.push({x:r.left+r.width/2,y:r.top+r.height/2,a:r.width*r.height}); });
 return JSON.stringify(out.slice(0,5)); })()";

    private const string InputSel = "input[placeholder*=\"erver name\" i],input[placeholder*=\"name or ip\" i]";

    private static readonly string TypeJs = @"(function(q){
 var i=document.querySelector('" + InputSel.Replace("'", "\\'") + @"');
 if(!i) return 'no-input';
 var set=Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype,'value').set;
 i.focus(); set.call(i,q);
 i.dispatchEvent(new Event('input',{bubbles:true})); i.dispatchEvent(new Event('change',{bubbles:true}));
 i.dispatchEvent(new KeyboardEvent('keyup',{bubbles:true,key:'a'}));
 return 'ok';})(";

    private static readonly string ListJs = @"(function(){
 var i=document.querySelector('" + InputSel.Replace("'", "\\'") + @"'); if(!i) return '[]';
 var root=i; for(var k=0;k<8&&root.parentElement;k++){root=root.parentElement; if(/dialog|modal|popup|search/i.test((root.className||'')+' '+(root.getAttribute('role')||''))) break;}
 var out=[]; var seen={};
 [].slice.call(root.querySelectorAll('a,li,tr,[role=option],[class*=result],[class*=server],[class*=item]')).forEach(function(e){
   if(e.contains(i)||i.contains(e)) return;
   var t=(e.innerText||'').replace(/\s+/g,' ').trim(); if(t.length<4||t.length>240||seen[t]) return;
   if(/advanced search|battlemetrics\.com|only servers with players/i.test(t)) return;
   var idx=out.length; e.setAttribute('data-rc-idx',idx); seen[t]=1;
   var a=(e.tagName==='A'?e:e.querySelector('a'));
   out.push({i:idx,t:t,h:(a&&a.href)||''});
 }); return JSON.stringify(out.slice(0,40)); })()";

    private const string ClickJs = @"(function(n){
 var e=document.querySelector('[data-rc-idx=""'+n+'""]'); if(!e) return 'gone';
 var a=(e.tagName==='A'?e:e.querySelector('a')); (a||e).click(); return 'ok';})(";

    private const string MapLinkJs = @"(function(){var a=document.querySelector('a[href*=""/map/""]'); return a?a.href:'';})()";

    private sealed record Row(int I, string T, string H);
    private sealed record Pt(double X, double Y, double A);

    private async Task<bool> HasInputAsync() => await RunAsync("(function(){return document.querySelector('" + InputSel.Replace("'", "\\'") + "')?'1':'0';})()") == "1";

    /// <summary>Настоящий (доверенный) щелчок мышью через DevTools: обычный element.click() у меню сайта не открывает окно поиска.</summary>
    private async Task RealClickAsync(double x, double y)
    {
        var c = _web.CoreWebView2; var ci = System.Globalization.CultureInfo.InvariantCulture;
        string P(string type, int cc) => "{\"type\":\"" + type + "\",\"x\":" + x.ToString(ci) + ",\"y\":" + y.ToString(ci) + ",\"button\":\"left\",\"buttons\":" + (type == "mouseReleased" ? 0 : 1) + ",\"clickCount\":" + cc + "}";
        await c.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", P("mouseMoved", 0));
        await c.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", P("mousePressed", 1));
        await c.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", P("mouseReleased", 1));
    }

    private async Task<List<Pt>> PointsAsync(string js)
    {
        var json = await RunAsync(js);
        return (json is null ? null : JsonSerializer.Deserialize<List<Pt>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })) ?? new();
    }

    /// <summary>Открывает окно «Server Search»: щелчок по пункту меню; в узком окне сначала раскрывается меню-«гамбургер».</summary>
    private async Task<bool> OpenSearchAsync(CancellationToken ct)
    {
        if (await HasInputAsync()) return true;
        for (var round = 0; round < 2; round++)
        {
            foreach (var p in await PointsAsync(FindMenuJs))
            {
                await RealClickAsync(p.X, p.Y); await Task.Delay(700, ct);
                if (await HasInputAsync()) return true;
            }
            await RunAsync(OpenJs);                          // запасной вариант: обычный click()
            await Task.Delay(600, ct);
            if (await HasInputAsync()) return true;
            foreach (var b in await PointsAsync(FindBurgerJs)) { await RealClickAsync(b.X, b.Y); await Task.Delay(500, ct); }
        }
        return await HasInputAsync();
    }

    private async Task<string?> RunAsync(string js)
    {
        var raw = await _web.CoreWebView2.ExecuteScriptAsync(js);
        try { return JsonSerializer.Deserialize<string>(raw); } catch (JsonException) { return null; }
    }

    private async Task NavigateAsync(string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        void H(object? s, CoreWebView2NavigationCompletedEventArgs e) { _web.CoreWebView2.NavigationCompleted -= H; tcs.TrySetResult(); }
        _web.CoreWebView2.NavigationCompleted += H;
        _web.CoreWebView2.Navigate(url);
        await Task.WhenAny(tcs.Task, Task.Delay(20000, ct)).ConfigureAwait(true);
        _web.CoreWebView2.NavigationCompleted -= H;
        await Task.Delay(800, ct).ConfigureAwait(true);   // скрипты страницы дорисовывают меню
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsMapPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("rustmaps.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith("/map/", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.Length > 5;

    /// <summary>Перебирает запросы (название, часть названия, IP) и возвращает адрес страницы карты найденного сервера; report — что происходило на каждом шаге.</summary>
    public async Task<(string? Url, string Report)> FindAsync(IEnumerable<string> queries, string serverName, CancellationToken ct)
    {
        var report = new List<string>();
        if (_web.CoreWebView2 is null) return (null, "встроенный браузер не запущен");
        var want = Norm(serverName); var core = Norm(string.Join("", serverName.Split('|')[0].Split(' ', '—', '-', '–', '#', '(', ')', '/').Where(w => w.Length > 0).Take(3)));
        foreach (var q in queries.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await NavigateAsync("https://rustmaps.com/", ct);
                if (!await OpenSearchAsync(ct)) { report.Add($"«{q}»: окно «Server Search» не открылось (пункт меню не найден или не реагирует)"); continue; }
                await Task.Delay(400, ct);
                var typed = await RunAsync(TypeJs + JsonSerializer.Serialize(q) + ")");
                if (typed != "ok") { report.Add($"«{q}»: поле «Server name or ip» не найдено"); continue; }
                List<Row> rows = new(); Row? pick = null;
                for (var i = 0; i < 16 && pick is null; i++)
                {
                    await Task.Delay(500, ct);
                    var json = await RunAsync(ListJs);
                    rows = (json is null ? null : JsonSerializer.Deserialize<List<Row>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })) ?? new();
                    pick = rows.Select(r => (r, s: Score(Norm(r.T), want, core))).Where(x => x.s >= 2).OrderByDescending(x => x.s).Select(x => x.r).FirstOrDefault();
                }
                if (pick is null) { report.Add($"«{q}»: результатов {rows.Count}, подходящего нет" + (rows.Count > 0 ? ": " + string.Join(" | ", rows.Take(4).Select(r => r.T)) : "")); continue; }
                report.Add($"«{q}»: выбран «{pick.T}»");
                if (IsMapPage(pick.H)) return (pick.H, string.Join("\n", report));
                await RunAsync(ClickJs + pick.I + ")");
                for (var i = 0; i < 24; i++)
                {
                    await Task.Delay(500, ct);
                    var cur = _web.Source?.ToString();
                    if (IsMapPage(cur)) return (cur, string.Join("\n", report));
                    if (cur is not null && cur.Contains("rustmaps.com", StringComparison.OrdinalIgnoreCase) && i >= 4 && !cur.TrimEnd('/').Equals("https://rustmaps.com", StringComparison.OrdinalIgnoreCase))
                    {
                        var link = await RunAsync(MapLinkJs);
                        if (IsMapPage(link)) return (link, string.Join("\n", report));
                    }
                }
                report.Add($"«{q}»: после выбора страница карты не открылась");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { report.Add($"«{q}»: {ex.Message}"); }
        }
        return (null, string.Join("\n", report));
    }

    private static int Score(string text, string want, string core)
    {
        var s = 0;
        if (want.Length > 0 && text.Contains(want)) s += 3;
        if (core.Length >= 5 && text.Contains(core)) s += 2;
        return s;
    }
}
