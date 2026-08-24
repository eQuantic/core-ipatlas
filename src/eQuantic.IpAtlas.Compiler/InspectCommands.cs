using System.Globalization;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Reading a dataset back: <c>verify</c> to prove one is intact and how old it
/// is, <c>lookup</c> to see what it actually answers. Both exist so a dataset
/// can be checked where it is deployed, not only where it was built.
/// </summary>
public static class InspectCommands
{
    /// <summary>Opens a dataset, checks it, and prints what it holds.</summary>
    public static int Verify(Arguments args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var path = args.One("dataset", required: true);
        var maxAgeDays = args.One("max-age-days");
        if (args.Errors.Count > 0)
        {
            foreach (var message in args.Errors)
            {
                error.WriteLine($"eqatlas: {message}");
            }

            return 2;
        }

        if (!IpAtlasDatabase.TryOpen(path!, out var database, out var failure))
        {
            error.WriteLine($"eqatlas: {failure}");
            return 1;
        }

        var db = database!;
        output.WriteLine($"  path      {path}");
        output.WriteLine($"  format    version {db.FormatVersion}");
        output.WriteLine($"  built     {db.BuiltAt:u}  ({db.Age.TotalDays:F1} days ago)");
        output.WriteLine($"  source    {db.Source}");
        output.WriteLine($"  ranges    {db.V4RangeCount:N0} IPv4, {db.V6RangeCount:N0} IPv6");
        output.WriteLine($"  places    {db.LocationCount:N0}");
        output.WriteLine("  checksum  verified");

        if (maxAgeDays is not null)
        {
            if (!double.TryParse(maxAgeDays, out var limit))
            {
                error.WriteLine($"eqatlas: --max-age-days '{maxAgeDays}' is not a number");
                return 2;
            }

            if (db.Age.TotalDays > limit)
            {
                error.WriteLine(
                    $"eqatlas: dataset is {db.Age.TotalDays:F1} days old, over the {limit:F0} day limit");
                return 1;
            }
        }

        return 0;
    }

    /// <summary>Looks addresses up against a dataset and prints every field it answered with.</summary>
    public static int Lookup(Arguments args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var path = args.One("dataset", required: true);
        var addresses = args.All("ip");
        if (addresses.Count == 0)
        {
            args.Fail("--ip needs at least one address");
        }

        if (args.Errors.Count > 0)
        {
            foreach (var message in args.Errors)
            {
                error.WriteLine($"eqatlas: {message}");
            }

            return 2;
        }

        if (!IpAtlasDatabase.TryOpen(path!, out var database, out var failure))
        {
            error.WriteLine($"eqatlas: {failure}");
            return 1;
        }

        foreach (var address in addresses)
        {
            var info = database!.Lookup(address);
            output.WriteLine(address);
            output.WriteLine($"  scope     {info.Scope}");
            output.WriteLine($"  country   {info.CountryCode ?? "-"}");
            output.WriteLine($"  asn       {info.Asn?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            output.WriteLine($"  traits    {(info.Traits == NetworkTraits.None ? "-" : info.Traits.ToString())}");
            if (info.Location is { } place)
            {
                var coordinates = place.HasCoordinates
                    ? $"{place.Latitude:F2}, {place.Longitude:F2}"
                    : "no coordinates";
                output.WriteLine($"  place     {place.City ?? "-"} / {place.Region ?? "-"} ({coordinates})");
                output.WriteLine($"  from      {place.Source}");
            }

            output.WriteLine();
        }

        return 0;
    }
}
