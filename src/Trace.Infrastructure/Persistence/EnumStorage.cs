using System.Text.Json;

namespace Trace.Infrastructure.Persistence;

/// <summary>Enums are stored as snake_case text so rows stay readable and reordering members is safe.</summary>
internal static class EnumStorage
{
    public static string ToStorage<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static TEnum FromStorage<TEnum>(string value)
        where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (ToStorage(candidate) == value)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"'{value}' is not a stored {typeof(TEnum).Name} value.");
    }

    /// <summary>SQL for a check constraint restricting <paramref name="column"/> to the enum's stored values.</summary>
    public static string CheckSql<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(value => $"'{ToStorage(value)}'"))})";
}
