namespace RustCompanion.Core.RustPlus;

public enum RpMarkerKind { Player, Radius, Unknown }

/// <summary>Маркер карты Rust+ (мировые координаты в метрах, начало — левый нижний угол).</summary>
public sealed record RpMapMarker(ulong Id, RpMarkerKind Kind, double X, double Y, string? Name = null, double? Radius = null, double? Rotation = null, ulong? SteamId = null);
public sealed record RpMapMonument(string Name, double X, double Y);
/// <summary>Карта сервера: JPEG-картинка, её размеры в пикселях, ширина океана по краям и размер мира в метрах.</summary>
public sealed record RpMap(byte[] Jpg, int Width, int Height, int OceanMargin, int MapSize, IReadOnlyList<RpMapMonument> Monuments);

public static class RpMapGeometry
{
    /// <summary>Мир → пиксели картинки (Y переворачивается): px = x·(W−2m)/size + m; py = H − (y·(H−2m)/size + m).</summary>
    public static (double X, double Y) ToPixel(double worldX, double worldY, int mapSize, int width, int height, int oceanMargin)
    {
        if (mapSize <= 0) return (0, 0);
        var sx = (width - 2.0 * oceanMargin) / mapSize; var sy = (height - 2.0 * oceanMargin) / mapSize;
        return (worldX * sx + oceanMargin, height - (worldY * sy + oceanMargin));
    }
    public static (double X, double Y) ToPixel(double worldX, double worldY, RpMap map) => ToPixel(worldX, worldY, map.MapSize, map.Width, map.Height, map.OceanMargin);

    /// <summary>Подпись маркера для карты. События без собственного имени (например Deep Sea приходит как «неизвестный» маркер) подписываются по возможности.</summary>
    public static string Label(RpMapMarker m) => m.Kind switch
    {
        RpMarkerKind.Player => string.IsNullOrWhiteSpace(m.Name) ? "Игрок" : m.Name!,
        RpMarkerKind.Radius => string.IsNullOrWhiteSpace(m.Name) ? "Зона события" : m.Name!,
        _ => string.IsNullOrWhiteSpace(m.Name) ? "Событие" : m.Name!
    };

    /// <summary>С патча Power Trip (06.08.2026) Rust+ больше не получает маркеры патрульного вертолёта, грузового корабля, странствующего торговца и торговых автоматов , Чинука, взрывов, заблокированных ящиков и аэрдропов — их на карте нет (Facepunch убрал отправку маркеров событий).</summary>
    public static bool IsEvent(RpMarkerKind k) => k != RpMarkerKind.Player;
}
