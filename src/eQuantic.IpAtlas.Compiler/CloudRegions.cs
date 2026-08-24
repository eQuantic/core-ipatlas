namespace eQuantic.IpAtlas.Compiler;

/// <summary>Where a cloud region physically is.</summary>
/// <param name="CountryCode">ISO 3166-1 alpha-2.</param>
/// <param name="City">The city the region is named for.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
public readonly record struct CloudRegion(string CountryCode, string City, double Latitude, double Longitude);

/// <summary>
/// Cloud region names mapped to the places they actually run in.
/// <para>
/// This table is why importing cloud ranges is worth doing. AWS, Google and
/// Microsoft register their address space to a single legal entity — almost
/// always in the United States — so a registry delegation puts every one of
/// their machines in the US, whichever continent it is really on. But all three
/// publish which region each prefix belongs to, and a region is a place. Join
/// the two and an address in Frankfurt stops claiming to be in Virginia.
/// </para>
/// Coordinates are the region's advertised metropolitan area, which is the
/// honest precision available: providers name the metro, not the building.
/// </summary>
public static class CloudRegions
{
    /// <summary>The place a provider region runs in, or null when the name is unknown.</summary>
    public static CloudRegion? Get(string? region) =>
        region is not null && Table.TryGetValue(region, out var place) ? place : null;

    /// <summary>How many regions the table knows.</summary>
    public static int Count => Table.Count;

    private static readonly Dictionary<string, CloudRegion> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // Amazon Web Services.
        ["us-east-1"] = new("US", "Ashburn", 39.04, -77.49),
        ["us-east-2"] = new("US", "Columbus", 39.96, -83.00),
        ["us-west-1"] = new("US", "San Jose", 37.35, -121.96),
        ["us-west-2"] = new("US", "Portland", 45.87, -119.69),
        ["us-gov-west-1"] = new("US", "Portland", 45.87, -119.69),
        ["us-gov-east-1"] = new("US", "Columbus", 39.96, -83.00),
        ["ca-central-1"] = new("CA", "Montreal", 45.50, -73.57),
        ["ca-west-1"] = new("CA", "Calgary", 51.04, -114.07),
        ["sa-east-1"] = new("BR", "Sao Paulo", -23.55, -46.63),
        ["mx-central-1"] = new("MX", "Queretaro", 20.59, -100.39),
        ["eu-west-1"] = new("IE", "Dublin", 53.35, -6.26),
        ["eu-west-2"] = new("GB", "London", 51.51, -0.13),
        ["eu-west-3"] = new("FR", "Paris", 48.86, 2.35),
        ["eu-central-1"] = new("DE", "Frankfurt", 50.11, 8.68),
        ["eu-central-2"] = new("CH", "Zurich", 47.38, 8.54),
        ["eu-north-1"] = new("SE", "Stockholm", 59.33, 18.06),
        ["eu-south-1"] = new("IT", "Milan", 45.46, 9.19),
        ["eu-south-2"] = new("ES", "Zaragoza", 41.65, -0.89),
        ["ap-northeast-1"] = new("JP", "Tokyo", 35.69, 139.69),
        ["ap-northeast-2"] = new("KR", "Seoul", 37.57, 126.98),
        ["ap-northeast-3"] = new("JP", "Osaka", 34.69, 135.50),
        ["ap-southeast-1"] = new("SG", "Singapore", 1.35, 103.82),
        ["ap-southeast-2"] = new("AU", "Sydney", -33.87, 151.21),
        ["ap-southeast-3"] = new("ID", "Jakarta", -6.21, 106.85),
        ["ap-southeast-4"] = new("AU", "Melbourne", -37.81, 144.96),
        ["ap-southeast-5"] = new("MY", "Kuala Lumpur", 3.14, 101.69),
        ["ap-southeast-6"] = new("NZ", "Auckland", -36.85, 174.76),
        ["ap-southeast-7"] = new("TH", "Bangkok", 13.76, 100.50),
        ["ap-south-1"] = new("IN", "Mumbai", 19.08, 72.88),
        ["ap-south-2"] = new("IN", "Hyderabad", 17.39, 78.49),
        ["ap-east-1"] = new("HK", "Hong Kong", 22.32, 114.17),
        ["ap-east-2"] = new("TW", "Taipei", 25.03, 121.57),
        ["me-south-1"] = new("BH", "Manama", 26.07, 50.56),
        ["me-central-1"] = new("AE", "Dubai", 25.20, 55.27),
        ["il-central-1"] = new("IL", "Tel Aviv", 32.09, 34.78),
        ["af-south-1"] = new("ZA", "Cape Town", -33.92, 18.42),
        ["cn-north-1"] = new("CN", "Beijing", 39.90, 116.41),
        ["cn-northwest-1"] = new("CN", "Yinchuan", 38.47, 106.27),

        // Google Cloud.
        ["us-central1"] = new("US", "Council Bluffs", 41.26, -95.86),
        ["us-east1"] = new("US", "Moncks Corner", 33.20, -80.01),
        ["us-east4"] = new("US", "Ashburn", 39.04, -77.49),
        ["us-east5"] = new("US", "Columbus", 39.96, -83.00),
        ["us-south1"] = new("US", "Dallas", 32.78, -96.80),
        ["us-west1"] = new("US", "The Dalles", 45.60, -121.18),
        ["us-west2"] = new("US", "Los Angeles", 34.05, -118.24),
        ["us-west3"] = new("US", "Salt Lake City", 40.76, -111.89),
        ["us-west4"] = new("US", "Las Vegas", 36.17, -115.14),
        ["northamerica-northeast1"] = new("CA", "Montreal", 45.50, -73.57),
        ["northamerica-northeast2"] = new("CA", "Toronto", 43.65, -79.38),
        ["northamerica-south1"] = new("MX", "Queretaro", 20.59, -100.39),
        ["southamerica-east1"] = new("BR", "Sao Paulo", -23.55, -46.63),
        ["southamerica-west1"] = new("CL", "Santiago", -33.45, -70.67),
        ["europe-west1"] = new("BE", "Saint-Ghislain", 50.45, 3.82),
        ["europe-west2"] = new("GB", "London", 51.51, -0.13),
        ["europe-west3"] = new("DE", "Frankfurt", 50.11, 8.68),
        ["europe-west4"] = new("NL", "Eemshaven", 53.43, 6.83),
        ["europe-west6"] = new("CH", "Zurich", 47.38, 8.54),
        ["europe-west8"] = new("IT", "Milan", 45.46, 9.19),
        ["europe-west9"] = new("FR", "Paris", 48.86, 2.35),
        ["europe-west10"] = new("DE", "Berlin", 52.52, 13.40),
        ["europe-west12"] = new("IT", "Turin", 45.07, 7.69),
        ["europe-north1"] = new("FI", "Hamina", 60.57, 27.19),
        ["europe-north2"] = new("SE", "Stockholm", 59.33, 18.06),
        ["europe-central2"] = new("PL", "Warsaw", 52.23, 21.01),
        ["europe-southwest1"] = new("ES", "Madrid", 40.42, -3.70),
        ["asia-east1"] = new("TW", "Changhua", 24.08, 120.54),
        ["asia-east2"] = new("HK", "Hong Kong", 22.32, 114.17),
        ["asia-northeast1"] = new("JP", "Tokyo", 35.69, 139.69),
        ["asia-northeast2"] = new("JP", "Osaka", 34.69, 135.50),
        ["asia-northeast3"] = new("KR", "Seoul", 37.57, 126.98),
        ["asia-south1"] = new("IN", "Mumbai", 19.08, 72.88),
        ["asia-south2"] = new("IN", "Delhi", 28.61, 77.21),
        ["asia-southeast1"] = new("SG", "Singapore", 1.35, 103.82),
        ["asia-southeast2"] = new("ID", "Jakarta", -6.21, 106.85),
        ["australia-southeast1"] = new("AU", "Sydney", -33.87, 151.21),
        ["australia-southeast2"] = new("AU", "Melbourne", -37.81, 144.96),
        ["me-west1"] = new("IL", "Tel Aviv", 32.09, 34.78),
        ["me-central1"] = new("QA", "Doha", 25.29, 51.53),
        ["me-central2"] = new("SA", "Dammam", 26.43, 50.10),
        ["africa-south1"] = new("ZA", "Johannesburg", -26.20, 28.05),

        // Microsoft Azure.
        ["eastus"] = new("US", "Ashburn", 39.04, -77.49),
        ["eastus2"] = new("US", "Ashburn", 39.04, -77.49),
        ["centralus"] = new("US", "Des Moines", 41.59, -93.62),
        ["northcentralus"] = new("US", "Chicago", 41.88, -87.63),
        ["southcentralus"] = new("US", "San Antonio", 29.42, -98.49),
        ["westcentralus"] = new("US", "Cheyenne", 41.14, -104.82),
        ["westus"] = new("US", "San Francisco", 37.77, -122.42),
        ["westus2"] = new("US", "Quincy", 47.23, -119.85),
        ["westus3"] = new("US", "Phoenix", 33.45, -112.07),
        ["canadacentral"] = new("CA", "Toronto", 43.65, -79.38),
        ["canadaeast"] = new("CA", "Quebec City", 46.81, -71.21),
        ["brazilsouth"] = new("BR", "Sao Paulo", -23.55, -46.63),
        ["brazilsoutheast"] = new("BR", "Rio de Janeiro", -22.91, -43.17),
        ["northeurope"] = new("IE", "Dublin", 53.35, -6.26),
        ["westeurope"] = new("NL", "Amsterdam", 52.37, 4.90),
        ["uksouth"] = new("GB", "London", 51.51, -0.13),
        ["ukwest"] = new("GB", "Cardiff", 51.48, -3.18),
        ["francecentral"] = new("FR", "Paris", 48.86, 2.35),
        ["francesouth"] = new("FR", "Marseille", 43.30, 5.37),
        ["germanywestcentral"] = new("DE", "Frankfurt", 50.11, 8.68),
        ["germanynorth"] = new("DE", "Berlin", 52.52, 13.40),
        ["switzerlandnorth"] = new("CH", "Zurich", 47.38, 8.54),
        ["switzerlandwest"] = new("CH", "Geneva", 46.20, 6.14),
        ["norwayeast"] = new("NO", "Oslo", 59.91, 10.75),
        ["norwaywest"] = new("NO", "Stavanger", 58.97, 5.73),
        ["swedencentral"] = new("SE", "Gavle", 60.67, 17.14),
        ["polandcentral"] = new("PL", "Warsaw", 52.23, 21.01),
        ["italynorth"] = new("IT", "Milan", 45.46, 9.19),
        ["spaincentral"] = new("ES", "Madrid", 40.42, -3.70),
        ["austriaeast"] = new("AT", "Vienna", 48.21, 16.37),
        ["uaenorth"] = new("AE", "Dubai", 25.20, 55.27),
        ["uaecentral"] = new("AE", "Abu Dhabi", 24.45, 54.38),
        ["qatarcentral"] = new("QA", "Doha", 25.29, 51.53),
        ["israelcentral"] = new("IL", "Tel Aviv", 32.09, 34.78),
        ["southafricanorth"] = new("ZA", "Johannesburg", -26.20, 28.05),
        ["southafricawest"] = new("ZA", "Cape Town", -33.92, 18.42),
        ["australiaeast"] = new("AU", "Sydney", -33.87, 151.21),
        ["australiasoutheast"] = new("AU", "Melbourne", -37.81, 144.96),
        ["australiacentral"] = new("AU", "Canberra", -35.28, 149.13),
        ["australiacentral2"] = new("AU", "Canberra", -35.28, 149.13),
        ["newzealandnorth"] = new("NZ", "Auckland", -36.85, 174.76),
        ["southeastasia"] = new("SG", "Singapore", 1.35, 103.82),
        ["eastasia"] = new("HK", "Hong Kong", 22.32, 114.17),
        ["japaneast"] = new("JP", "Tokyo", 35.69, 139.69),
        ["japanwest"] = new("JP", "Osaka", 34.69, 135.50),
        ["koreacentral"] = new("KR", "Seoul", 37.57, 126.98),
        ["koreasouth"] = new("KR", "Busan", 35.18, 129.08),
        ["centralindia"] = new("IN", "Pune", 18.52, 73.86),
        ["southindia"] = new("IN", "Chennai", 13.08, 80.27),
        ["westindia"] = new("IN", "Mumbai", 19.08, 72.88),
        ["indonesiacentral"] = new("ID", "Jakarta", -6.21, 106.85),
        ["malaysiawest"] = new("MY", "Kuala Lumpur", 3.14, 101.69),
        ["chinanorth"] = new("CN", "Beijing", 39.90, 116.41),
        ["chinaeast"] = new("CN", "Shanghai", 31.23, 121.47),
    };
}
