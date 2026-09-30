using System.Text;
using RpfToFiveM.Core.Extraction;
using RpfToFiveM.Tests.Fixtures;

namespace RpfToFiveM.Tests;

public sealed class ResumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpf2fivem_resume_" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "src");
    private string Output => Path.Combine(_root, "out");
    private string JobDir => Path.Combine(_root, "job");

    public ResumeTests() => Directory.CreateDirectory(Source);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void WriteSample(int files = 6)
    {
        var b = new RpfBuilder();
        for (int i = 0; i < files; i++) b.AddText($"data/file{i}.txt", $"content {i}");
        b.WriteTo(Path.Combine(Source, "pack.rpf"));
    }

    private Task<ExtractionResult> Run(IProgressJournal? journal, CancellationToken ct = default) =>
        new RpfExtractor().RunAsync(new ExtractionOptions
        {
            SourceFolder = Source,
            OutputFolder = Output,
            Mode = OutputMode.Dump,
            Journal = journal,
        }, null, null, new PauseController(), ct);

    /// <summary>Simulates a crash by cancelling once a number of files have been journaled.</summary>
    private sealed class CrashingJournal : IProgressJournal
    {
        private readonly IProgressJournal _inner;
        private readonly CancellationTokenSource _cts;
        private readonly int _crashAfter;

        public CrashingJournal(IProgressJournal inner, CancellationTokenSource cts, int crashAfter)
        {
            _inner = inner;
            _cts = cts;
            _crashAfter = crashAfter;
        }

        public int CompletedCount => _inner.CompletedCount;
        public bool IsCompleted(string key) => _inner.IsCompleted(key);

        public void MarkCompleted(string key)
        {
            _inner.MarkCompleted(key);
            if (_inner.CompletedCount >= _crashAfter) _cts.Cancel();
        }
    }

    [Fact]
    public void FileJournal_PersistsAcrossInstances()
    {
        var path = Path.Combine(_root, "journal.log");
        using (var j = new FileJournal(path))
        {
            j.MarkCompleted(@"a\one.ydr");
            j.MarkCompleted(@"a\two.ydr");
        }

        using var reopened = new FileJournal(path);
        Assert.True(reopened.IsCompleted(@"a\one.ydr"));
        Assert.True(reopened.IsCompleted(@"A\TWO.YDR"));
        Assert.False(reopened.IsCompleted(@"a\three.ydr"));
        Assert.Equal(2, reopened.CompletedCount);
    }

    [Fact]
    public void FileJournal_IgnoresLineTornByACrash()
    {
        var path = Path.Combine(_root, "journal.log");
        using (var j = new FileJournal(path)) j.MarkCompleted("done.ydr");
        File.AppendAllText(path, "half-writ", Encoding.UTF8);

        using var reopened = new FileJournal(path);
        Assert.Equal(1, reopened.CompletedCount);
        Assert.False(reopened.IsCompleted("half-writ"));
    }

    [Fact]
    public void JobStore_RoundTripsAndClears()
    {
        var store = new JobStore(JobDir);
        Assert.Null(store.Load());

        var job = new JobInfo
        {
            SourceFolder = Source,
            OutputFolder = Output,
            GameFolder = @"C:\Games\GTAV",
            Mode = OutputMode.FiveM,
            ResourceName = "my_map",
            FilesTotal = 42,
        };
        store.Save(job);
        using (var journal = store.OpenJournal()) journal.MarkCompleted("x.ydr");

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(Source, loaded!.SourceFolder);
        Assert.Equal(OutputMode.FiveM, loaded.Mode);
        Assert.Equal(42, loaded.FilesTotal);
        Assert.Equal(1, store.CompletedCount());

        store.Clear();
        Assert.Null(store.Load());
        Assert.Equal(0, store.CompletedCount());
    }

    [Fact]
    public void JobStore_CorruptJobFileIsTreatedAsNoJob()
    {
        Directory.CreateDirectory(JobDir);
        File.WriteAllText(Path.Combine(JobDir, "job.json"), "{ not json");
        Assert.Null(new JobStore(JobDir).Load());
    }

    [Fact]
    public async Task Extractor_SkipsFilesAlreadyInJournal()
    {
        WriteSample();
        var done = Path.Combine(Output, "extracted", "pack.rpf", "data", "file0.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(done)!);
        File.WriteAllText(done, "from previous run");
        using var journal = new FileJournal(Path.Combine(_root, "j.log"));
        journal.MarkCompleted(Path.Combine("pack.rpf", "data", "file0.txt"));

        var result = await Run(journal);

        Assert.Equal("from previous run", File.ReadAllText(done));
        Assert.Equal(1, result.FilesResumed);
        Assert.Equal(6, result.FilesWritten);
        Assert.Equal(6, journal.CompletedCount);
    }

    [Fact]
    public async Task Extractor_RedoesJournaledFileWhoseOutputIsMissing()
    {
        WriteSample();
        using var journal = new FileJournal(Path.Combine(_root, "j.log"));
        journal.MarkCompleted(Path.Combine("pack.rpf", "data", "file0.txt"));

        var result = await Run(journal);

        Assert.Equal("content 0", File.ReadAllText(Path.Combine(Output, "extracted", "pack.rpf", "data", "file0.txt")));
        Assert.Equal(0, result.FilesResumed);
    }

    [Fact]
    public async Task Extractor_ResumesAfterCrashWithoutRedoingFinishedFiles()
    {
        WriteSample(files: 10);
        var journalPath = Path.Combine(_root, "j.log");

        using (var cts = new CancellationTokenSource())
        using (var journal = new FileJournal(journalPath))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(new CrashingJournal(journal, cts, 4), cts.Token));
        }

        using var resumed = new FileJournal(journalPath);
        var result = await Run(resumed);

        Assert.Equal(4, result.FilesResumed);
        Assert.Equal(10, result.FilesWritten);
        for (int i = 0; i < 10; i++)
            Assert.Equal($"content {i}", File.ReadAllText(Path.Combine(Output, "extracted", "pack.rpf", "data", $"file{i}.txt")));
    }

    [Fact]
    public async Task FiveMResume_StillRegistersYtypsFromEarlierRun()
    {
        new RpfBuilder().AddResource("a.ytyp", new byte[] { 1 }).AddResource("b.ydr", new byte[] { 2 })
            .WriteTo(Path.Combine(Source, "pack.rpf"));
        var journalPath = Path.Combine(_root, "j.log");
        using (var first = new FileJournal(journalPath))
            await new RpfExtractor().RunAsync(new ExtractionOptions { SourceFolder = Source, OutputFolder = Output, Mode = OutputMode.FiveM, Journal = first },
                null, null, new PauseController());
        File.Delete(Path.Combine(Output, "my_map", "fxmanifest.lua"));

        using var second = new FileJournal(journalPath);
        var result = await new RpfExtractor().RunAsync(new ExtractionOptions { SourceFolder = Source, OutputFolder = Output, Mode = OutputMode.FiveM, Journal = second },
            null, null, new PauseController());

        Assert.Equal(2, result.FilesResumed);
        Assert.Contains("a.ytyp", File.ReadAllText(Path.Combine(Output, "my_map", "fxmanifest.lua")));
    }
}
