using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cmms.LoadDataRunner;

/// <summary>
/// Turns one column's JSON value back into the CLR value SqlBulkCopy must hand to SQL Server.
///
/// The artifact holds MODEL values, because the generator wrote <c>entry.Property(name).CurrentValue</c>.
/// So the trip back is JSON to model type, then through the property's value converter if it has one, then
/// out of any enum into its underlying integer. Skipping the converter step would write the model value
/// where the provider value belongs, which for a converted property is silently the wrong data rather than
/// a failure.
///
/// Enums are unwrapped explicitly rather than left as enum instances. EF maps them through a type mapping
/// with no converter, so nothing upstream unwraps them, and handing SqlBulkCopy a boxed enum leans on
/// conversion behaviour worth not relying on for 42 million rows.
/// </summary>
public sealed class ColumnBinder
{
    public string ColumnName { get; }
    public Type FieldType { get; }

    /// <summary>
    /// Whether the column must be supplied. An artifact generated before an ADDITIVE migration legitimately
    /// lacks the new column, and for a nullable one that is harmless: the bulk copy simply does not supply
    /// it and the row gets NULL, exactly as a row written before that migration would have. Only a required
    /// column makes the artifact unloadable.
    /// </summary>
    public bool IsRequired { get; }
    private readonly Func<System.Text.Json.JsonElement, object?> _parse;

    private ColumnBinder(string columnName, Type fieldType, bool isRequired, Func<System.Text.Json.JsonElement, object?> parse)
    {
        ColumnName = columnName;
        FieldType = fieldType;
        IsRequired = isRequired;
        _parse = parse;
    }

    public object? Read(System.Text.Json.JsonElement e) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Null ? null : _parse(e);

    public static ColumnBinder For(IProperty property)
    {
        var columnName = property.GetColumnName()
            ?? throw new InvalidOperationException($"{property.Name} has no mapped column.");

        var converter = property.GetValueConverter();
        var modelType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var parseToModel = ParserFor(modelType, columnName);

        // The type SqlBulkCopy will see, after the converter and after unwrapping an enum.
        var providerType = converter?.ProviderClrType ?? modelType;
        providerType = Nullable.GetUnderlyingType(providerType) ?? providerType;
        if (providerType.IsEnum) providerType = Enum.GetUnderlyingType(providerType);

        return new ColumnBinder(columnName, providerType, !property.IsNullable, e =>
        {
            var value = parseToModel(e);
            if (converter is not null) value = converter.ConvertToProvider(value);
            if (value is not null && value.GetType().IsEnum)
                value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture);
            return value;
        });
    }

    private static Func<System.Text.Json.JsonElement, object?> ParserFor(Type t, string column)
    {
        if (t.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(t);
            return e => Enum.ToObject(t, Convert.ChangeType(e.GetInt64(), underlying, CultureInfo.InvariantCulture));
        }

        if (t == typeof(Guid)) return e => e.GetGuid();
        if (t == typeof(string)) return e => e.GetString();
        if (t == typeof(bool)) return e => e.GetBoolean();
        if (t == typeof(byte)) return e => e.GetByte();
        if (t == typeof(short)) return e => e.GetInt16();
        if (t == typeof(int)) return e => e.GetInt32();
        if (t == typeof(long)) return e => e.GetInt64();
        if (t == typeof(decimal)) return e => e.GetDecimal();
        if (t == typeof(double)) return e => e.GetDouble();
        if (t == typeof(float)) return e => e.GetSingle();
        if (t == typeof(DateTimeOffset)) return e => e.GetDateTimeOffset();
        if (t == typeof(DateTime)) return e => e.GetDateTime();
        if (t == typeof(byte[])) return e => e.GetBytesFromBase64();

        // DateOnly and TimeOnly round-trip as strings through System.Text.Json rather than as a native
        // JSON type, so they are parsed back with the same invariant round-trip formats.
        if (t == typeof(DateOnly))
            return e => DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (t == typeof(TimeOnly))
            return e => TimeOnly.Parse(e.GetString()!, CultureInfo.InvariantCulture);
        if (t == typeof(TimeSpan))
            return e => TimeSpan.Parse(e.GetString()!, CultureInfo.InvariantCulture);

        throw new NotSupportedException(
            $"Column '{column}' has CLR type {t.Name}, which the loader has no conversion for. " +
            "Add one rather than letting the value be coerced.");
    }
}
