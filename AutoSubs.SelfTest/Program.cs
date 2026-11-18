using Jellyfin.Plugin.AutoSubs.Core;

var sandbox = Path.Combine(Path.GetTempPath(), "autosubs-selftest-" + Guid.NewGuid().ToString("N"));
try
{
    var movie = Path.Combine(sandbox, "La.La.Land");
    Directory.CreateDirectory(Path.Combine(movie, "Subs", "Spanish"));
    File.WriteAllText(Path.Combine(movie, "La.La.Land.mkv"), "video placeholder");
    File.WriteAllText(Path.Combine(movie, "Subs", "Spanish", "arbitrary.idx"), "idx placeholder");
    File.WriteAllText(Path.Combine(movie, "Subs", "Spanish", "arbitrary.sub"), "sub placeholder");
    File.WriteAllText(Path.Combine(movie, "Subs", "English.srt"), "srt placeholder");

    var runner = new SubtitleOrganizer();
    var dry = runner.Scan(sandbox, true, 10, CancellationToken.None, Console.WriteLine);
    Assert(dry.Links == 3 && Directory.EnumerateFiles(movie).Count() == 1, "dry-run does not write");
    var first = runner.Scan(sandbox, false, 10, CancellationToken.None, Console.WriteLine);
    Assert(first.Movies == 1 && first.Links == 3, "creates 3 symlinks for VobSub pair and SRT");
    var second = runner.Scan(sandbox, false, 10, CancellationToken.None, Console.WriteLine);
    Assert(second.Links == 0, "second scan idempotent");
    var links = Directory.EnumerateFiles(movie).Where(f => new FileInfo(f).LinkTarget is not null).ToArray();
    Assert(links.Length == 3, "all three symlinks present");
    Assert(links.Count(f => f.EndsWith(".idx", StringComparison.OrdinalIgnoreCase)) == 1, "vobsub index linked");
    Assert(links.Count(f => f.EndsWith(".sub", StringComparison.OrdinalIgnoreCase)) == 1, "vobsub data linked");

    var mixed = Path.Combine(sandbox, "Ambiguous");
    Directory.CreateDirectory(mixed);
    File.WriteAllText(Path.Combine(mixed, "A.mkv"), "a");
    File.WriteAllText(Path.Combine(mixed, "B.mkv"), "b");
    File.WriteAllText(Path.Combine(mixed, "unknown.srt"), "subs");
    var third = runner.Scan(mixed, false, 3, CancellationToken.None, Console.WriteLine);
    Assert(third.AmbiguousFolders == 1 && third.Links == 0, "ambiguous movies skipped");

    Console.WriteLine("ALL TESTS PASSED");
}
finally
{
    Directory.Delete(sandbox, recursive: true);
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("TEST FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}
