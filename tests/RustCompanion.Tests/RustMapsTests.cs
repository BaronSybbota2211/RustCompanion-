using RustCompanion.Core.RustPlus;
using Xunit;

namespace RustCompanion.Tests;

public class RustMapsTests
{
    [Fact]
    public void Parse_reads_links_and_monuments()
    {
        const string json = """
        {"meta":{"status":"Success"},"data":{"id":"abc","seed":1234,"size":4000,"isStaging":false,"isCustomMap":false,
         "url":"https://rustmaps.com/map/abc","imageUrl":"https://content.rustmaps.com/i.png","imageIconUrl":"https://content.rustmaps.com/ic.png",
         "monuments":[{"type":"Airfield","coordinates":{"x":100.5,"y":-200}},{"displayName":"Dome","x":1,"y":2}]}}
        """;
        var i = RustMapsClient.Parse(json, 4000, 1234);
        Assert.NotNull(i);
        Assert.Equal("https://content.rustmaps.com/ic.png", i!.ImageIconUrl);
        Assert.Equal(2, i.Monuments.Count);
        Assert.Equal(100.5, i.Monuments[0].X);
        Assert.Equal("Dome", i.Monuments[1].Name);
    }

    [Fact] public void Parse_returns_null_on_garbage() => Assert.Null(RustMapsClient.Parse("not json", 1, 1));
    [Fact] public void ViewerUrl_uses_size_and_seed() => Assert.Equal("https://rustmaps.com/map/4000_1234", RustMapsClient.ViewerUrl(4000, 1234));
}

public class RustMapsLayerTests
{
    [Fact]
    public void Layers_are_collected_from_nested_objects_and_arrays_and_titled()
    {
        const string json = """
        {"data":{"id":"a","imageUrl":"https://content.rustmaps.com/main.png","thumbnailUrl":"https://content.rustmaps.com/t.png",
         "monuments":[{"type":"Dome","iconUrl":"https://content.rustmaps.com/i.png"}],
         "heatmaps":{"bears":"https://content.rustmaps.com/bears.png","nodes":"https://content.rustmaps.com/nodes.png"},
         "overlays":[{"name":"noBuild","url":"https://content.rustmaps.com/nobuild.png"}],
         "other":"https://example.com/x.png"}}
        """;
        var i = RustMapsClient.Parse(json, 4000, 1)!;
        Assert.Equal(3, i.Layers!.Count);
        Assert.Contains(i.Layers, l => l.Title.StartsWith("Медведи"));
        Assert.Contains(i.Layers, l => l.Title.StartsWith("Зона запрета строительства"));
        Assert.DoesNotContain(i.Layers, l => l.Url.Contains("main.png") || l.Url.Contains("i.png") || l.Url.Contains("example.com"));
    }
}

public class RustMapsLocatorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(2147483647, true)]
    [InlineData(3040981880, false)]
    [InlineData(-1, false)]
    public void Seed_must_fit_int32(long seed, bool ok) => Assert.Equal(ok, RustMapsLocator.IsValidSeed(seed));

    [Fact]
    public void Pick_takes_custom_map_id_from_battlemetrics_details()
    {
        const string json = """
        {"data":[
          {"attributes":{"name":"Other server","details":{"rust_world_size":3000,"rust_maps":{"url":"https://rustmaps.com/map/111111111111111111111111111111aa","size":3000,"seed":5}}}},
          {"attributes":{"name":"Magic Rust #17 - Modded 2x (Solo/Duo) | Вайп 09.11","details":{"rust_world_size":4000,"rust_world_seed":3040981880,
            "rust_maps":{"url":"https://rustmaps.com/map/30e4c14b699a4999a2c3c088a4f0c7b6","size":4000,"seed":3040981880}}}}]}
        """;
        var t = RustMapsLocator.Pick(json, "Magic Rust #17 — Modded 2x (Solo/Duo) | Вайп 09.11", 4000, 3040981880);
        Assert.NotNull(t);
        Assert.Equal("30e4c14b699a4999a2c3c088a4f0c7b6", t!.MapId);
        Assert.Equal("https://rustmaps.com/map/30e4c14b699a4999a2c3c088a4f0c7b6", t.ViewerUrl);
    }

    [Fact]
    public void Pick_rejects_server_with_other_map_size()
    {
        const string json = """{"data":[{"attributes":{"name":"Magic Rust 17","details":{"rust_world_size":3000,"rust_maps":{"url":"https://rustmaps.com/map/3000_1","size":3000,"seed":1}}}}]}""";
        Assert.Null(RustMapsLocator.Pick(json, "Magic Rust 17", 4000, 77));
    }
}

public class RustMapsBindingTests
{
    [Fact]
    public void FromUrl_distinguishes_custom_id_and_size_seed_and_rejects_foreign_hosts()
    {
        Assert.Equal("abc123def456", RustMapsLocator.FromUrl("https://rustmaps.com/map/abc123def456", 4000, 3040981880)!.MapId);
        var t = RustMapsLocator.FromUrl("https://rustmaps.com/map/4000_1234", null, null)!;
        Assert.Null(t.MapId); Assert.Equal(4000, t.Size); Assert.Equal(1234, t.Seed);
        Assert.Null(RustMapsLocator.FromUrl("https://evil.example.com/map/abc123def456", 1, 1));
    }

    [Fact]
    public void PickDetailed_reports_why_nothing_was_chosen()
    {
        const string json = """{"data":[{"attributes":{"name":"Magic Rust #17 - Modded 2x","details":{"rust_world_size":4000,"rust_world_seed":3040981880}}}]}""";
        var (t, note) = RustMapsLocator.PickDetailed(json, "Magic Rust #17 — Modded 2x (Solo/Duo) | Вайп 09.11", 4000, 3040981880);
        Assert.Null(t);
        Assert.Contains("Magic Rust #17", note);
        Assert.Contains("ссылка RustMaps нет", note);
    }

    [Fact]
    public async Task Binding_is_stored_per_server_and_can_be_cleared()
    {
        var e = new RpEnv(); await e.PairAsync();
        var store = new RustMapsBindingStore(e.Db);
        Assert.Null(await store.GetAsync(e.Server.Id));
        await store.SetAsync(e.Server.Id, new RustMapsBinding("https://rustmaps.com/map/abc123def456", 4000, 3040981880));
        var b = await store.GetAsync(e.Server.Id);
        Assert.Equal(3040981880, b!.Seed);
        await store.ClearAsync(e.Server.Id);
        Assert.Null(await store.GetAsync(e.Server.Id));
    }
}

public class RustMapsLayerStoreTests
{
    [Fact]
    public async Task Captured_layers_are_deduplicated_and_named_from_file()
    {
        var e = new RpEnv(); await e.PairAsync();
        var store = new RustMapsLayerStore(e.Db);
        Assert.True(await store.AddAsync(e.Server.Id, "https://content.rustmaps.com/maps/1/abc/heatmap_bears.png"));
        Assert.False(await store.AddAsync(e.Server.Id, "https://content.rustmaps.com/maps/1/abc/heatmap_bears.png"));
        Assert.True(await store.AddAsync(e.Server.Id, "https://content.rustmaps.com/maps/1/abc/noBuild.png"));
        var l = await store.GetAsync(e.Server.Id);
        Assert.Equal(2, l.Count);
        Assert.StartsWith("Медведи", l[0].Title);
        Assert.StartsWith("Зона запрета строительства", l[1].Title);
    }
}
