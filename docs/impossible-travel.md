# Impossible travel

[← docs index](README.md)

Could the same person have got from where the last sign-in came from to where
this one did, in the time between them?

```csharp
using eQuantic.IpAtlas.Geo;

var verdict = Velocity.Assess(db.Lookup(lastIp), db.Lookup(thisIp), elapsed);

if (verdict.Plausible == false)
{
    // the geometry rules it out
}
```

## Three answers, not two

`Plausible` is `bool?`. `false` means the geometry rules it out. `true` means it
does not. **`null` means the data cannot carry the question**, and it is the
answer this library works hardest to give correctly, because the alternative is
a signal that looks confident and is not.

```csharp
if (verdict.Plausible == false)   // right
if (verdict.Plausible != true)    // wrong: treats "cannot tell" as impossible
```

`Reason` says which case you got:

| `Reason` | when |
|---|---|
| `Assessed` | a speed was computed and judged |
| `NotAPersonsLocation` | an address is anycast or an anonymizer |
| `OutOfOrder` | the events arrived out of order |
| `CountryTooLarge` | both sightings in one country too wide to tell from a centroid |
| `NotLocated` | nothing located one side or the other |

## Why each case declines

### Anycast and anonymizers

A Cloudflare address is announced from thirty cities at once. A VPN exit is
where the service is, not where the user is. Both have a location, and reading
it as the person's is how impossible-travel checks manufacture false positives
at scale.

```csharp
Velocity.Assess(db.Lookup("104.16.0.1"), lisbon, TimeSpan.FromMinutes(10));
// Plausible = null, Reason = NotAPersonsLocation
```

### Events out of order

A negative interval is clock skew, a retried webhook, or an out-of-order queue.
It is not a person moving backwards.

```csharp
Velocity.Assess("PT", "JP", TimeSpan.FromHours(-14));
// Plausible = null, Reason = OutOfOrder
```

Reading a negative interval as zero time — which is what the obvious
implementation does — turns every clock-skew event into a fraud alert.

### One country, too wide to tell

Two sightings both in Russia, ten minutes apart, could be one office or they
could be Kaliningrad and Vladivostok, 6,400 km away. A country centroid cannot
distinguish them.

```csharp
Velocity.Assess("RU", "RU", TimeSpan.FromMinutes(10));   // null, CountryTooLarge
Velocity.Assess("RU", "RU", TimeSpan.FromHours(12));     // true: even the worst case is reachable
Velocity.Assess("BE", "BE", TimeSpan.FromMinutes(10));   // true: Belgium is crossable, always
```

`CountrySpans` records how wide each country is; anything at or below 1,500 km
is treated as unremarkable to be anywhere in. Only countries wider than that are
listed, so anything absent from the table is small by construction.

Give the pair real coordinates and the question becomes answerable:

```csharp
var newYork    = new IpInfo("US", 1, location: new IpLocation(40.71, -74.01, "US-NY", "New York", LocationSource.Geofeed));
var losAngeles = new IpInfo("US", 2, location: new IpLocation(34.05, -118.24, "US-CA", "Los Angeles", LocationSource.Geofeed));

Velocity.Assess(newYork, losAngeles, TimeSpan.FromMinutes(20));
// Plausible = false, ~3,940 km, Precision = Coordinates
```

## Precision

```csharp
verdict.Precision   // Coordinates | Country | None
```

`Coordinates` when a geofeed or cloud provider supplied real coordinates for
both sides — two cities inside one country become distinguishable. `Country`
when it fell back to centroids, which are accurate to a degree or two and only
ever answer continental questions.

Weight the signal accordingly. A `false` at `Coordinates` precision is a much
stronger claim than one at `Country`.

## The speed ceiling

The default is 950 km/h, generously above a commercial flight. Pass your own
where you know better:

```csharp
Velocity.Assess("PT", "ES", TimeSpan.FromHours(1), maxKilometersPerHour: 120);
// false at train speed, true at flight speed
```

Overloads deliberately carry no optional parameters, so the ceiling a call uses
never changes because a signature grew.

## Country codes that are not places

`EU` and `AP` appear in registry data for allocations spanning a whole region.
They have no centroid and never will: giving them a point would turn "somewhere
across a continent" into a specific place a distance could be measured from.
They resolve to `NotLocated`.

Territories and dependencies that registries do emit — `RE`, `GF`, `GP`, `MQ`,
`GL`, `VG`, `KY`, `IM`, `GG`, `JE`, `BM`, `CW`, `XK` and forty others — do have
centroids. Réunion is 9,000 km from mainland France, and answering "unknown" for
it in a risk product is the signal going quiet exactly over the offshore
jurisdictions it exists to notice.

## What this signal cannot do

- It cannot see a user who never moved but whose traffic changed exit. That is
  what `IsAnonymizer` and `IsHosting` are for.
- It cannot resolve movement inside a city; nothing free can.
- It is one input. A `false` at country precision means "these two countries are
  far apart", which is evidence, not a verdict.
