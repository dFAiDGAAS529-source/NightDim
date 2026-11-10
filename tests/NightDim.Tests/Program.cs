using System.Runtime.CompilerServices;
using NightDim.Core;

var display = new DisplayInfo("test-panel", "DISPLAY1", "Test", 0, 0, 1920, 1080, true, HdrState.Sdr);
var tests = new (string Name, Action Run)[]
{
    ("LUT size, order, range and monotone channels", LutShape),
    ("Normalization of limits and non-finite inputs", Normalization),
    ("Neutral retains a calibrated baseline and writes nothing", Neutral),
    ("Independent RGB gains and blue attenuation", IndependentChannels),
    ("Temperature and gamma compose in one curve", TemperatureAndGamma),
    ("Full range monotonicity and distinct night levels", BrightnessMonotonicity),
    ("Gamma safety constrains an all-zero RGB request", ConstrainsBlackGamma),
    ("Defaults and profile calculations are deterministic", DeterministicProfiles),
    ("Settings round-trip, migration and corruption recovery", SettingsRoundTrip),
    ("Gamma readback, hybrid and verified restoration", VerifiedGamma),
    ("Unchanged TRUE readback falls back without a rewrite loop", SilentRejection),
    ("Write failure falls back to Overlay", WriteRejection),
    ("HDR, unknown and unstable outputs skip gamma", IneligibleGamma),
    ("Missing overlay reports partial or unavailable effect", MissingOverlay),
    ("Probe preserves active overlay", OverlayProbe),
    ("Foreign LUT change relinquishes ownership", ForeignWriter),
    ("Quantized readback uses exact ownership for restoration", QuantizedReadback),
    ("Post-write read failure retains recovery state", ReadFailureAfterWrite),
    ("Read failure after a working LUT never stacks a full overlay", BlocksUncertainFallback),
    ("Unconfirmed gamma prevents an extreme overlay", BlocksUncertainExtreme),
    ("Fixed overlay keeps the brightness curve and skips gamma access", FixedOverlay),
    ("Switching to fixed overlay releases only our LUT", FixedOverlayReleasesOwnedRamp),
    ("Persisted gamma uncertainty blocks both overlay modes", PersistedGammaUncertainty),
    ("Unavailable journal prevents unsafe writes", UnavailableJournal),
    ("External changes during journaling prevent the write", RecheckAfterJournal),
    ("Unreadable pre-write state does not leave a candidate to recover", UnreadableBeforeWrite),
    ("Crash recovery restores only exact matching readbacks", CrashRecovery),
    ("Emergency restore removes overlay before gamma write", EmergencyOrder)
};

int failed = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}\n{ex}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

void LutShape()
{
    var settings = new EffectSettings(32, 3650, 0.61, 23, 0.9, 0.8, 0.7);
    GammaRamp ramp = GammaCurve.Build(GammaRamp.Identity, settings, 0.8);
    Check(ramp.Values.Length == 768);
    for (int channel = 0; channel < 3; channel++)
    {
        ReadOnlySpan<ushort> values = ramp.Values.Slice(channel * 256, 256);
        Check(values[0] == 0 && values[^1] > 0);
        for (int i = 1; i < values.Length; i++)
            Check(values[i] >= values[i - 1]);
    }

    Check(ramp.Values[255] > ramp.Values[511] && ramp.Values[511] > ramp.Values[767]);
}

void Normalization()
{
    var invalid = new EffectSettings(-5, 99999, double.NaN, 102, -1, 8, double.PositiveInfinity);
    Check(invalid.Normalize() == new EffectSettings(1, 8000, 1, 100, 0, 1, 1));
    Check(new EffectSettings(101, 100, -1, -3).Normalize() == new EffectSettings(100, 2000, 0.5));
    Check(new EffectSettings(Gamma: 5).Normalize().Gamma == 2);
    Check(GammaCurve.Build(GammaRamp.Identity, EffectSettings.Neutral, double.NaN).Matches(GammaRamp.Identity));
}

void Neutral()
{
    GammaRamp calibrated = GammaCurve.Build(GammaRamp.Identity, new EffectSettings(Red: 0.94, Blue: 0.92), 1);
    Check(GammaCurve.Build(calibrated, EffectSettings.Neutral, 1).Matches(calibrated));
    var gamma = new FakeGamma { Current = calibrated };
    var overlay = new FakeOverlay();
    var engine = Engine(gamma, overlay);
    Check(engine.Apply(display, EffectSettings.Neutral).Mechanism == EffectMechanism.Neutral);
    Check(gamma.Reads == 0 && gamma.Writes == 0 && overlay.Opacity == 0);
}

void IndependentChannels()
{
    var neutral = EffectSettings.Neutral;
    GammaRamp red = GammaCurve.Build(GammaRamp.Identity, neutral with { Red = 0.75 }, 1);
    GammaRamp green = GammaCurve.Build(GammaRamp.Identity, neutral with { Green = 0.75 }, 1);
    GammaRamp blue = GammaCurve.Build(GammaRamp.Identity, neutral with { Blue = 0.5, BlueReduction = 20 }, 1);
    Check(red.Values[255] == 49151 && red.Values[511] == ushort.MaxValue && red.Values[767] == ushort.MaxValue);
    Check(green.Values[255] == ushort.MaxValue && green.Values[511] == 49151 && green.Values[767] == ushort.MaxValue);
    Check(blue.Values[255] == ushort.MaxValue && blue.Values[511] == ushort.MaxValue && blue.Values[767] == 26214);
}

void TemperatureAndGamma()
{
    var warm = new EffectSettings(Temperature: 3200);
    GammaRamp temperature = GammaCurve.Build(GammaRamp.Identity, warm, 1);
    GammaRamp combined = GammaCurve.Build(GammaRamp.Identity, warm with { Gamma = 1.5 }, 1);
    Check(temperature.Values[255] == combined.Values[255]);
    Check(temperature.Values[767] == combined.Values[767]);
    Check(combined.Values[128] > temperature.Values[128]);
    Check(combined.Values[640] > temperature.Values[640]);
    Check(combined.Values[640] < combined.Values[128]);
    GammaRamp cold = GammaCurve.Build(GammaRamp.Identity, warm with { Temperature = 8000 }, 1);
    Check(cold.Values[767] == ushort.MaxValue && cold.Values[255] < ushort.MaxValue);
}

void BrightnessMonotonicity()
{
    foreach (HdrState mode in new[] { HdrState.Sdr, HdrState.Hdr })
    {
        var gamma = new FakeGamma();
        var overlay = new FakeOverlay();
        var engine = Engine(gamma, overlay);
        double previous = 0;
        for (int brightness = 1; brightness <= 100; brightness++)
        {
            DisplayEffectResult result = engine.Apply(display with { Hdr = mode }, new EffectSettings(brightness));
            double signal = gamma.Current.Values[255] * (255 - result.OverlayOpacity);
            Check(signal >= previous);
            if (brightness <= 10)
                Check(signal > previous && result.OverlayOpacity < 252);
            previous = signal;
        }

        Check(gamma.Current.Matches(GammaRamp.Identity) && overlay.Opacity == 0);
    }
}

void ConstrainsBlackGamma()
{
    var gamma = new FakeGamma();
    var engine = Engine(gamma, new FakeOverlay());
    DisplayEffectResult result = engine.Apply(display, new EffectSettings(Gamma: 0.5, Red: 0, Green: 0, Blue: 0));
    Check(result.GammaApplied && result.Status == EffectDiagnostic.ReadbackConfirmedLimited);
    Check(gamma.Current.Values[128] >= 7864);
    Check(gamma.Current.Values[255] >= ushort.MaxValue - 32000);
    Check(engine.Restore(display));
}

void DeterministicProfiles()
{
    EffectSettings night = ProfileManager.DefaultProfiles["night"];
    Check(night == new EffectSettings(10, 3200));
    GammaRamp first = GammaCurve.Build(GammaRamp.Identity, night, 0.8);
    GammaRamp second = GammaCurve.Build(GammaRamp.Identity, night, 0.8);
    Check(first.Matches(second));
}

void SettingsRoundTrip() => InTempDirectory(path =>
{
    string file = Path.Combine(path, "settings.json");
    var manager = new ProfileManager(file);
    var settings = new Preferences
    {
        Enabled = false, OverlayOnly = true, Language = "en", Synchronize = true, SelectedDisplayId = display.Id,
        Synchronized = new EffectSettings(20, 5000),
        Displays = { [display.Id] = new EffectSettings(50) },
        Profiles = { ["custom"] = new EffectSettings(101, 9000, 1.4) }
    };

    Check(manager.Save(settings));
    Preferences saved = manager.Load();
    Check(!saved.Enabled && saved.OverlayOnly && saved.Language == "en" && saved.Synchronize && saved.SelectedDisplayId == display.Id);
    Check(saved.Synchronized == settings.Synchronized && saved.Displays[display.Id] == settings.Displays[display.Id]);
    Check(saved.Profiles["custom"] == new EffectSettings(100, 8000, 1.4));
    Check(saved.Profiles.Count == 5 && saved.Profiles["neutral"] == EffectSettings.Neutral);
    Check(Directory.GetFiles(path, "*.tmp").Length == 0);

    settings.SelectedDisplayId = "session:transient";
    settings.Displays[settings.SelectedDisplayId] = new EffectSettings(40);
    Check(manager.Save(settings));
    Preferences persistent = manager.Load();
    Check(persistent.SelectedDisplayId is null && !persistent.Displays.ContainsKey("session:transient"));
    Check(settings.Displays.ContainsKey("session:transient"));

    File.WriteAllText(file, """{"Profiles":null,"Displays":{"discard":null},"Synchronized":null}""");
    Preferences migrated = manager.Load();
    Check(!migrated.OverlayOnly && migrated.Language == "ru" && migrated.Profiles.Count == 5 && migrated.Displays.Count == 0
        && migrated.Synchronized == EffectSettings.Neutral);
    File.WriteAllText(file, "{");
    Check(manager.Load().Profiles["night"] == ProfileManager.DefaultProfiles["night"]);
});

void VerifiedGamma()
{
    var gamma = new FakeGamma();
    var overlay = new FakeOverlay();
    var engine = Engine(gamma, overlay);
    var settings = new EffectSettings(10, 3200);
    DisplayEffectResult result = engine.Apply(display, settings);
    Check(result.Mechanism == EffectMechanism.Hybrid && result.OverlayOpacity == 175);
    Check(gamma.Writes == 1 && engine.Apply(display, settings).GammaApplied && gamma.Writes == 1);
    Check(engine.Restore(display));
    Check(gamma.Current.Matches(GammaRamp.Identity) && gamma.Writes == 2 && overlay.Opacity == 0);
}

void SilentRejection()
{
    var gamma = new FakeGamma { WriteOverride = _ => true };
    var engine = Engine(gamma, new FakeOverlay());
    DisplayEffectResult first = engine.Apply(display, new EffectSettings(10));
    DisplayEffectResult second = engine.Apply(display, new EffectSettings(5));
    Check(first.Mechanism == EffectMechanism.Overlay && first.OverlayOpacity == 191);
    Check(second.Mechanism == EffectMechanism.Overlay && second.OverlayOpacity == 213);
    Check(gamma.Writes == 1);
}

void WriteRejection()
{
    var gamma = new FakeGamma { WriteOverride = _ => false };
    var engine = Engine(gamma, new FakeOverlay());
    Check(engine.Apply(display, new EffectSettings(1)).OverlayOpacity == 239);
    Check(gamma.Writes == 1 && engine.Restore(display));
}

void IneligibleGamma()
{
    var gamma = new FakeGamma();
    var engine = Engine(gamma, new FakeOverlay());
    foreach (DisplayInfo unsupported in new[]
    {
        display with { Hdr = HdrState.Hdr }, display with { Hdr = HdrState.Unknown },
        display with { CanRecoverGamma = false }
    })
    {
        Check(engine.Apply(unsupported, new EffectSettings(10)).Mechanism == EffectMechanism.Overlay);
    }

    Check(gamma.Reads == 0 && gamma.Writes == 0);
}

void MissingOverlay()
{
    var gamma = new FakeGamma();
    var overlay = new FakeOverlay { Available = false };
    var engine = Engine(gamma, overlay);
    Check(!engine.ProbeOverlay(display));
    DisplayEffectResult partial = engine.Apply(display, new EffectSettings(10));
    Check(partial.Mechanism == EffectMechanism.Gamma && !partial.CanPreviewExtreme);
    Check(partial.AdditionalDiagnostic == EffectDiagnostic.OverlayUnavailable);
    Check(engine.Restore(display));
    Check(engine.Apply(display with { Hdr = HdrState.Hdr }, new EffectSettings(10)).Mechanism == EffectMechanism.Unavailable);
}

void OverlayProbe()
{
    var overlay = new FakeOverlay();
    var engine = Engine(new FakeGamma(), overlay);
    Check(engine.ProbeOverlay(display) && overlay.Opacity == 0);
    engine.Apply(display, new EffectSettings(50));
    byte before = overlay.Opacity;
    Check(before > 0 && engine.ProbeOverlay(display) && overlay.Opacity == before);
}

void ForeignWriter()
{
    var gamma = new FakeGamma();
    var engine = Engine(gamma, new FakeOverlay());
    Check(engine.Apply(display, new EffectSettings(50)).GammaApplied);
    GammaRamp foreign = GammaCurve.Build(GammaRamp.Identity, new EffectSettings(Gamma: 1.2), 1);
    gamma.Current = foreign;
    Check(engine.Apply(display, new EffectSettings(30)).Mechanism == EffectMechanism.Overlay);
    Check(engine.Apply(display, new EffectSettings(40)).Mechanism == EffectMechanism.Overlay);
    Check(engine.Restore(display) && gamma.Current.Matches(foreign) && gamma.Writes == 1);
}

void QuantizedReadback()
{
    var gamma = new FakeGamma();
    gamma.WriteOverride = requested =>
    {
        ushort[] observed = requested.Values.ToArray();
        observed[255]--;
        gamma.Current = requested.Matches(GammaRamp.Identity) ? requested : new GammaRamp(observed);
        return true;
    };
    var journal = new MemoryGammaJournal();
    var engine = Engine(gamma, new FakeOverlay(), journal);
    Check(engine.Apply(display, new EffectSettings(20)).GammaApplied);
    Check(journal.TryRead(display.Id, out GammaJournalEntry? saved) && saved?.Applied?.Matches(gamma.Current) == true);
    Check(engine.Restore(display) && gamma.Current.Matches(GammaRamp.Identity));
}

void ReadFailureAfterWrite()
{
    var gamma = new FakeGamma();
    gamma.WriteOverride = requested =>
    {
        gamma.Current = requested;
        gamma.CanRead = false;
        return true;
    };
    var journal = new MemoryGammaJournal();
    var overlay = new FakeOverlay();
    var engine = Engine(gamma, overlay, journal);
    DisplayEffectResult result = engine.Apply(display, new EffectSettings(70));
    Check(result.Mechanism == EffectMechanism.Unavailable && overlay.Opacity == 0);
    Check(result.AdditionalDiagnostic == EffectDiagnostic.DimmingSuspended);
    Check(!engine.Restore(display));
    Check(journal.TryRead(display.Id, out GammaJournalEntry? saved) && saved?.Candidate?.Matches(gamma.Current) == true);
    gamma.CanRead = true;
    gamma.WriteOverride = null;
    Check(engine.Restore(display) && gamma.Current.Matches(GammaRamp.Identity));
}

void BlocksUncertainFallback()
{
    var gamma = new FakeGamma();
    var overlay = new FakeOverlay();
    var journal = new MemoryGammaJournal();
    var engine = Engine(gamma, overlay, journal);
    Check(engine.Apply(display, new EffectSettings(40)).Mechanism == EffectMechanism.Hybrid);

    gamma.CanRead = false;
    DisplayEffectResult blocked = engine.Apply(display, new EffectSettings(35));
    Check(blocked.Mechanism == EffectMechanism.Unavailable && overlay.Opacity == 0);
    Check(blocked.Status == EffectDiagnostic.ReadbackUnavailable &&
        blocked.AdditionalDiagnostic == EffectDiagnostic.DimmingSuspended);
    Check(journal.TryRead(display.Id, out GammaJournalEntry? saved) && saved?.Owns(gamma.Current) == true);
    Check(gamma.Writes == 1);

    gamma.CanRead = true;
    Check(engine.Apply(display, new EffectSettings(40)).OverlayOpacity == 108);
    Check(gamma.Current.Matches(GammaRamp.Identity) && gamma.Writes == 2);
    Check(engine.Apply(display, new EffectSettings(35)).Mechanism == EffectMechanism.Overlay);
    Check(gamma.Writes == 2); // Only restoration was retried, not the failed gamma path.
}

void BlocksUncertainExtreme()
{
    var gamma = new FakeGamma();
    gamma.WriteOverride = requested =>
    {
        gamma.Current = requested;
        gamma.CanRead = false;
        return true;
    };
    var overlay = new FakeOverlay();
    var engine = Engine(gamma, overlay);
    DisplayEffectResult result = engine.Apply(display, new EffectSettings(5));
    Check(result.Mechanism == EffectMechanism.Unavailable && !result.CanPreviewExtreme && overlay.Opacity == 0);
    Check(result.Status == EffectDiagnostic.PostWriteReadbackUnavailable &&
        result.AdditionalDiagnostic == EffectDiagnostic.DimmingSuspended);
}

void FixedOverlay()
{
    var gamma = new FakeGamma { CanRead = false };
    var engine = Engine(gamma, new FakeOverlay());
    var settings = new EffectSettings(40, 3000, 1.2);

    DisplayEffectResult result = engine.Apply(display, settings, overlayOnly: true);
    Check(result.Mechanism == EffectMechanism.Overlay && result.Status == EffectDiagnostic.OverlayOnly);
    Check(result.OverlayOpacity == 108 && !result.GammaApplied);
    Check(engine.Apply(display, new EffectSettings(40), overlayOnly: true) == result);
    Check(engine.Apply(display, settings with { Brightness = 100 }, overlayOnly: true).Mechanism == EffectMechanism.Neutral);
    Check(gamma.Reads == 0 && gamma.Writes == 0);
}

void FixedOverlayReleasesOwnedRamp()
{
    var gamma = new FakeGamma();
    var overlay = new FakeOverlay();
    var engine = Engine(gamma, overlay);
    var settings = new EffectSettings(40, 3000);
    Check(engine.Apply(display, settings).Mechanism == EffectMechanism.Hybrid);
    byte priorOpacity = overlay.Opacity;
    gamma.WriteOverride = requested =>
    {
        Check(overlay.Opacity == priorOpacity && requested.Matches(GammaRamp.Identity));
        gamma.Current = requested;
        return true;
    };

    Check(engine.Apply(display, settings, overlayOnly: true).OverlayOpacity == 108);
    Check(gamma.Current.Matches(GammaRamp.Identity) && gamma.Writes == 2);
    gamma.WriteOverride = null;

    Check(engine.Apply(display, settings).Mechanism == EffectMechanism.Hybrid);
    GammaRamp foreign = GammaCurve.Build(GammaRamp.Identity, new EffectSettings(Gamma: 1.2), 1);
    gamma.Current = foreign;
    Check(engine.Apply(display, settings, overlayOnly: true).OverlayOpacity == 108);
    Check(gamma.Current.Matches(foreign) && gamma.Writes == 3);
}

void PersistedGammaUncertainty()
{
    var gamma = new FakeGamma();
    var journal = new MemoryGammaJournal();
    var settings = new EffectSettings(40);
    Check(Engine(gamma, new FakeOverlay(), journal).Apply(display, settings).GammaApplied);
    gamma.CanRead = false;

    var overlay = new FakeOverlay();
    var restarted = Engine(gamma, overlay, journal);
    DisplayEffectResult fallback = restarted.Apply(display, settings);
    Check(fallback.Status == EffectDiagnostic.JournalReadFailed &&
        fallback.AdditionalDiagnostic == EffectDiagnostic.DimmingSuspended && overlay.Opacity == 0);

    DisplayInfo hdr = display with { Hdr = HdrState.Hdr, CanRecoverGamma = false };
    DisplayEffectResult fixedMode = restarted.Apply(hdr, settings, overlayOnly: true);
    Check(fixedMode.Status == EffectDiagnostic.OverlayOnly &&
        fixedMode.AdditionalDiagnostic == EffectDiagnostic.DimmingSuspended && overlay.Opacity == 0);
    Check(journal.TryRead(display.Id, out GammaJournalEntry? saved) && saved?.Owns(gamma.Current) == true);
    Check(gamma.Writes == 1);

    gamma.CanRead = true;
    Check(restarted.Apply(display, settings, overlayOnly: true).OverlayOpacity == 108);
    Check(gamma.Current.Matches(GammaRamp.Identity) && gamma.Writes == 2);
}

void UnavailableJournal()
{
    var gamma = new FakeGamma();
    var journal = new TestJournal { AllowWrites = false };
    var engine = Engine(gamma, new FakeOverlay(), journal);
    Check(engine.Apply(display, new EffectSettings(10)).Mechanism == EffectMechanism.Overlay);
    Check(gamma.Writes == 0);
}

void RecheckAfterJournal()
{
    var gamma = new FakeGamma();
    GammaRamp foreign = GammaCurve.Build(GammaRamp.Identity, new EffectSettings(Gamma: 1.2), 1);
    var journal = new TestJournal { AfterSave = () => gamma.Current = foreign };
    var engine = Engine(gamma, new FakeOverlay(), journal);
    Check(engine.Apply(display, new EffectSettings(30)).Mechanism == EffectMechanism.Overlay);
    Check(gamma.Writes == 0 && engine.Restore(display) && gamma.Current.Matches(foreign));
}

void UnreadableBeforeWrite()
{
    var gamma = new FakeGamma();
    var journal = new TestJournal { AfterSave = () => gamma.CanRead = false };
    var engine = Engine(gamma, new FakeOverlay(), journal);
    Check(engine.Apply(display, new EffectSettings(30)).Mechanism == EffectMechanism.Overlay);
    Check(gamma.Writes == 0);
    Check(journal.TryRead(display.Id, out GammaJournalEntry? entry) && entry is null);
}

void CrashRecovery() => InTempDirectory(path =>
{
    var gamma = new FakeGamma();
    var journal = new FileGammaJournal(path);
    var active = Engine(gamma, new FakeOverlay(), journal);
    Check(active.Apply(display, new EffectSettings(40)).GammaApplied);
    Check(active.Recover(display) && gamma.Writes == 1);

    var restarted = Engine(gamma, new FakeOverlay(), new FileGammaJournal(path));
    Check(!restarted.Recover(display with { Hdr = HdrState.Hdr }) && gamma.Writes == 1);
    Check(restarted.Recover(display) && gamma.Current.Matches(GammaRamp.Identity) && gamma.Writes == 2);
    Check(journal.TryRead(display.Id, out GammaJournalEntry? saved) && saved is null);

    var prior = Engine(gamma, new FakeOverlay(), journal);
    Check(prior.Apply(display, new EffectSettings(40)).GammaApplied);
    ushort[] foreign = gamma.Current.Values.ToArray();
    foreign[255]--;
    gamma.Current = new GammaRamp(foreign);
    Check(Engine(gamma, new FakeOverlay(), journal).Recover(display));
    Check(gamma.Writes == 3 && gamma.Current.Values[255] == foreign[255]);
});

void EmergencyOrder()
{
    var gamma = new FakeGamma();
    var overlay = new FakeOverlay();
    var engine = Engine(gamma, overlay);
    Check(engine.Apply(display, new EffectSettings(10)).Mechanism == EffectMechanism.Hybrid);
    gamma.WriteOverride = requested =>
    {
        Check(overlay.Opacity == 0);
        gamma.Current = requested;
        return true;
    };
    Check(engine.Restore(display));
}

static BrightnessEngine Engine(FakeGamma gamma, FakeOverlay overlay, IGammaJournal? journal = null) =>
    new(gamma, overlay, journal ?? new MemoryGammaJournal());

static void Check(bool result, [CallerArgumentExpression(nameof(result))] string? expression = null)
{
    if (!result)
        throw new InvalidOperationException(expression);
}

static void InTempDirectory(Action<string> action)
{
    // Use the build directory so the same-filesystem rename works in sandboxed test runs.
    string path = Path.Combine(AppContext.BaseDirectory, $"scratch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    try { action(path); }
    finally { Directory.Delete(path, recursive: true); }
}

sealed class FakeGamma : IGammaTransport
{
    public GammaRamp Current { get; set; } = GammaRamp.Identity;
    public bool CanRead { get; set; } = true;
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public Func<GammaRamp, bool>? WriteOverride { get; set; }

    public bool TryRead(DisplayInfo display, out GammaRamp? ramp)
    {
        Reads++;
        ramp = CanRead ? Current : null;
        return CanRead;
    }

    public bool TryWrite(DisplayInfo display, GammaRamp ramp)
    {
        Writes++;
        if (WriteOverride is not null)
            return WriteOverride(ramp);
        Current = ramp;
        return true;
    }
}

sealed class FakeOverlay : IOverlayBackend
{
    public byte Opacity { get; private set; }
    public bool Available { get; set; } = true;

    public bool TrySetOpacity(DisplayInfo display, byte opacity)
    {
        if (!Available)
            return false;
        Opacity = opacity;
        return true;
    }

    public void Remove(string displayId) => Opacity = 0;
}

sealed class TestJournal : IGammaJournal
{
    private readonly MemoryGammaJournal _inner = new();
    public bool AllowWrites { get; init; } = true;
    public Action? AfterSave { get; init; }

    public bool TryRead(string id, out GammaJournalEntry? entry) => _inner.TryRead(id, out entry);

    public bool TrySave(string id, GammaJournalEntry entry)
    {
        if (!AllowWrites)
            return false;
        bool saved = _inner.TrySave(id, entry);
        AfterSave?.Invoke();
        return saved;
    }

    public bool TryRemove(string id) => _inner.TryRemove(id);
}
