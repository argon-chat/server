namespace Argon.Features.EF;

using System.Drawing;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public class ColourConverter() : ValueConverter<Color, int>(x => ToInt(x), x => ToColor(x))
{
    private static Color ToColor(int val)
        => Color.FromArgb(val);

    private static int ToInt(Color val)
        => val.ToArgb();
}