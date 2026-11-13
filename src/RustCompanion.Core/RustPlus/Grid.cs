using System.Text;

namespace RustCompanion.Core.RustPlus;

/// <summary>Квадраты карты Rust (146,28 м): буквы по X слева направо (A…Z, AA…), номера по Y сверху вниз начиная с 0.</summary>
public static class RpGrid
{
    public const double CellSize = 146.28;

    public static string Letters(int index)
    {
        var sb = new StringBuilder(); index++;
        while (index > 0) { index--; sb.Insert(0, (char)('A' + index % 26)); index /= 26; }
        return sb.ToString();
    }

    /// <summary>Квадрат вида «K12» или null, если координат/размера карты нет либо точка вне карты.</summary>
    public static string? Cell(double? x, double? y, int? mapSize)
    {
        if (x is null || y is null || mapSize is not > 0) return null;
        if (x < 0 || y < 0 || x > mapSize || y > mapSize) return null;
        var cells = (int)Math.Ceiling(mapSize.Value / CellSize);
        var col = Math.Clamp((int)Math.Floor(x.Value / CellSize), 0, cells - 1);
        var row = Math.Clamp((int)Math.Floor((mapSize.Value - y.Value) / CellSize), 0, cells - 1);
        return Letters(col) + row;
    }
}
