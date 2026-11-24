using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.InitialModel.InitialReference3D;
using Refund.Jobs.Refinement.Refinement3D.Refine3D;

namespace Refund.Tests.Jobs;

/// <summary>In-place continuation of stopped relion_refine jobs (e.g. after a cluster time limit).</summary>
[Collection("JobRegistry")]
public sealed class RelionContinuationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-continue-" + Guid.NewGuid().ToString("N"));

    public RelionContinuationTests()
    {
        JobRegistry.EnsurePopulated();
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private T Connect<T>(T job) where T : Job
    {
        job.Id = 12;
        job.Space = new Space { RootDirectory = _root };
        string star = Path.Combine(_root, "particles.star");
        File.WriteAllText(star, "data_particles\nloop_\n_rlnImageName\n1@stack.mrcs\n");
        var particles = new ParticleSet { HasData = true, ParticlesSingleStarPath = star };
        foreach (var port in job.PortsIn.Values)
        {
            var producer = new Class2D { Id = 1, Space = job.Space };
            PortOut source = port.ResourceType == typeof(ParticleSet)
                ? new PortOut(producer, typeof(ParticleSet), "Particles", "Particles", _ => particles)
                : port.ResourceType == typeof(MapList)
                    ? new PortOut(producer, typeof(MapList), "Map", "Map",
                                  _ => new MapList([new Map(averageVolumePath: Path.Combine(_root, "map.mrc"))]))
                    : null;
            if (source != null)
                port.Edges.Add(new Edge { Source = source, Target = port });
        }
        Directory.CreateDirectory(job.DirectoryPath);
        Directory.CreateDirectory(job.RelayResultsDirectoryPath);
        job.Status = JobStatus.Failed;
        return job;
    }

    private static void WriteIteration(Job job, int iteration, params string[] suffixes)
    {
        foreach (string suffix in suffixes)
            File.WriteAllText(Path.Combine(job.DirectoryPath, $"run_it{iteration:D3}_{suffix}"),
                              suffix == "optimiser.star" ? "data_optimiser_general\n_rlnOutputRootName 12/run\n" : "x");
    }

    private static readonly string[] Complete = ["optimiser.star", "model.star", "data.star", "sampling.star", "class001.mrc"];

    [Fact]
    public void ContinuesFromLatestCompletelyWrittenIteration()
    {
        var job = Connect(new Class3D { NIterations = 25 });
        WriteIteration(job, 4, Complete);
        WriteIteration(job, 5, Complete);
        // Killed while writing iteration 6: the optimiser comes first, sampling last.
        WriteIteration(job, 6, "optimiser.star", "model.star", "class001.mrc");

        Assert.Equal(5, job.FindContinuableIteration());
        Assert.Null(job.WhyCannotContinue());
    }

    [Fact]
    public void RefusesWhenNotStoppedNothingWrittenOrAllIterationsDone()
    {
        var job = Connect(new Class3D { NIterations = 5 });
        Assert.NotNull(job.WhyCannotContinue()); // nothing written yet

        WriteIteration(job, 3, Complete);
        job.Status = JobStatus.Building;
        Assert.NotNull(job.WhyCannotContinue());
        job.Status = JobStatus.Finished;
        Assert.NotNull(job.WhyCannotContinue());
        job.Status = JobStatus.Aborted;
        Assert.Null(job.WhyCannotContinue());

        WriteIteration(job, 5, Complete);
        Assert.Contains("Finalize", job.WhyCannotContinue());
        Assert.Throws<InvalidOperationException>(job.BeginContinuation);
    }

    [Fact]
    public void StagingKeepsEarlierResultsAndRemovesWhatTheInterruptedIterationLeft()
    {
        var job = Connect(new Class3D { NIterations = 25 });
        WriteIteration(job, 5, Complete);
        WriteIteration(job, 6, "optimiser.star", "model.star", "class001.mrc");
        string relay = job.RelayResultsDirectoryPath;
        foreach (string name in new[] { "log_it0005.txt", "log_it0006.txt", "stats_it0006.json", "fsc_it0006_class001.png",
                                        "card_0005.png", "card_0006.png", "error.txt" })
            File.WriteAllText(Path.Combine(relay, name), "x");
        File.WriteAllText(job.PathStdOut, "old stdout");
        File.WriteAllText(Path.Combine(job.DirectoryPath, "RELION_JOB_EXIT_FAILURE"), "");
        job.VisAvailableIteration = 6;
        job.LogsAvailableIteration = 7;
        job.HasRunToCompletion = true;

        job.BeginContinuation();
        Assert.True(job.IsContinuingRun);
        Assert.Equal(5, job.ContinuedFromRelionIteration);
        Assert.Equal(6, job.ContinuingFromIteration);
        Assert.Equal(5, job.VisAvailableIteration);
        Assert.Equal(5, job.LogsAvailableIteration);
        Assert.False(job.HasRunToCompletion);

        job.Stage(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(job.DirectoryPath, "run_it005_optimiser.star")));
        Assert.False(File.Exists(Path.Combine(job.DirectoryPath, "run_it006_optimiser.star")));
        Assert.False(File.Exists(Path.Combine(job.DirectoryPath, "run_it006_class001.mrc")));
        Assert.Equal(new[] { "card_0005.png", "error.txt", "log_it0005.txt" },
                     Directory.GetFiles(relay).Select(Path.GetFileName).Order());
        Assert.False(File.Exists(job.PathStdOut));
        Assert.Equal("old stdout", File.ReadAllText(job.PathStdOut + ".until_it005"));
        Assert.False(File.Exists(Path.Combine(job.DirectoryPath, "RELION_JOB_EXIT_FAILURE")));
        // Inputs aren't staged again.
        Assert.False(Directory.Exists(Path.Combine(job.DirectoryPath, "inputs")));
    }

    [Fact]
    public void ArgumentsContinueTheSameRunWithComputeOptionsOnly()
    {
        var job = Connect(new Class3D { NIterations = 25, NThreads = 6, AdditionalArguments = "--maxsig 500 --pad 1" });
        WriteIteration(job, 5, Complete);
        var fresh = job.ComposeCommandArguments();
        Assert.Equal("6", fresh["j"]);

        job.BeginContinuation();
        var args = job.ComposeCommandArguments();

        Assert.Equal("12/run_it005_optimiser.star", args["continue"]);
        // The optimiser stores "12/run"; the same --o would make RELION write run_ct5_it006_* instead.
        Assert.Equal("12/./run", args["o"]);
        Assert.Equal("6", args["j"]);
        Assert.Equal("500", args["maxsig"]);
        Assert.Equal("12", args["pipeline_control"]);
        foreach (string key in new[] { "i", "ref", "K", "sym", "iter", "ctf", "tau2_fudge", "healpix_order", "pad", "firstiter_cc", "norm" })
            Assert.False(args.ContainsKey(key), key);
    }

    [Fact]
    public void OutputRootAvoidsWhicheverSpellingTheOptimiserStored()
    {
        var job = Connect(new Class3D { NIterations = 25 });
        WriteIteration(job, 5, Complete);
        // Written by a previous continuation that used the alternative spelling.
        File.WriteAllText(Path.Combine(job.DirectoryPath, "run_it005_optimiser.star"),
                          "data_optimiser_general\n\n_rlnOutputRootName                    12/./run\n");

        job.BeginContinuation();

        Assert.Equal("12/run", job.ComposeCommandArguments()["o"]);
    }

    [Fact]
    public void PooledContinuationKeepsPoolArgumentsAndDropsOldCoordinationData()
    {
        var job = Connect(new Class3D { NIterations = 25, UseWorkerPool = true, PoolQueueId = 1, ParticlesPerTask = 64 });
        WriteIteration(job, 5, Complete);
        Directory.CreateDirectory(Path.Combine(job.DirectoryPath, "pool", "tasks"));

        job.BeginContinuation();
        job.Stage(CancellationToken.None);
        var args = job.ComposeCommandArguments();

        Assert.False(Directory.Exists(Path.Combine(job.DirectoryPath, "pool")));
        Assert.Equal("12/pool", args["pool_dir"]);
        Assert.Equal("64", args["pool_batch"]);
        Assert.Equal("16", args["j"]);
        Assert.Equal("12/run_it005_optimiser.star", args["continue"]);
    }

    [Fact]
    public void NormalRunAfterContinuationStartsFromScratch()
    {
        var job = Connect(new Class3D { NIterations = 25 });
        WriteIteration(job, 5, Complete);
        job.BeginContinuation();

        job.EndContinuation();

        Assert.False(job.IsContinuingRun);
        Assert.Equal(0, job.ContinuingFromIteration);
        Assert.Equal("12/run", job.ComposeCommandArguments()["o"]);
        Assert.False(job.ComposeCommandArguments().ContainsKey("continue"));
    }

    [Fact]
    public void Refine3DNeedsBothHalfModels()
    {
        var job = Connect(new Refine3D());
        WriteIteration(job, 7, "optimiser.star", "half1_model.star", "half2_model.star", "data.star", "sampling.star");
        WriteIteration(job, 8, "optimiser.star", "half1_model.star", "data.star", "sampling.star");

        Assert.Equal(7, job.FindContinuableIteration());
        job.BeginContinuation();
        var args = job.ComposeCommandArguments();
        Assert.Equal("12/run_it007_optimiser.star", args["continue"]);
        foreach (string key in new[] { "i", "ref", "auto_refine", "split_random_halves", "solvent_mask" })
            Assert.False(args.ContainsKey(key), key);
    }

    [Fact]
    public void Refine3DLogsOfTheContinuationKeepAbsoluteIterationNumbers()
    {
        var job = Connect(new Refine3D());
        WriteIteration(job, 7, "optimiser.star", "half1_model.star", "half2_model.star", "data.star", "sampling.star");
        job.LogsAvailableIteration = 8;
        job.BeginContinuation();
        File.WriteAllText(job.LogFilePath(0), "first run preamble");
        File.WriteAllText(job.PathStdOut, "preamble\n Auto-refine: Iteration= 8\neight\n Auto-refine: Iteration= 9\nnine\n");

        job.TrackProgressLogs()?.Invoke();

        Assert.Equal(9, job.LogsAvailableIteration);
        Assert.Equal("first run preamble", File.ReadAllText(job.LogFilePath(0)));
        Assert.Contains("preamble", File.ReadAllText(job.LogFilePath(8)));
        Assert.Contains("eight", File.ReadAllText(job.LogFilePath(8)));
        Assert.Contains("nine", File.ReadAllText(job.LogFilePath(9)));
    }

    [Fact]
    public void Class2DVdamContinuesOnlyFromWrittenResultsAndMapsLogicalIterations()
    {
        var job = Connect(new Class2D { Algorithm = Class2DAlgorithm.VDAM, NIterationsVDAM = 200 });
        WriteIteration(job, 50, Complete);
        // VDAM writes optimiser files only every 10 iterations; 55 can't be a logical result.
        WriteIteration(job, 55, Complete);

        Assert.Equal(50, job.FindContinuableIteration());
        job.BeginContinuation();
        Assert.Equal(6, job.ContinuingFromIteration);
        Assert.Equal("12/run_it050_optimiser.star", job.ComposeCommandArguments()["continue"]);
        Assert.Equal("10", job.ComposeCommandArguments()["grad_write_iter"]);

        // stdout starts at RELION iteration 51; logical iteration 7 starts at RELION iteration 61.
        var lines = new List<string> { "preamble" };
        for (int it = 51; it <= 62; it++)
        {
            lines.Add(" CurrentResolution= 10");
            lines.Add($"iteration {it}");
        }
        File.WriteAllText(Path.Combine(job.DirectoryPath, "run.out"), string.Join('\n', lines));

        job.TrackProgressLogs()?.Invoke();

        Assert.Equal(7, job.LogsAvailableIteration);
        Assert.Contains("iteration 51", File.ReadAllText(job.LogFilePath(6)));
        Assert.Contains("iteration 60", File.ReadAllText(job.LogFilePath(6)));
        Assert.DoesNotContain("iteration 61", File.ReadAllText(job.LogFilePath(6)));
        Assert.StartsWith(" CurrentResolution", File.ReadAllText(job.LogFilePath(7)));
        Assert.Contains("iteration 61", File.ReadAllText(job.LogFilePath(7)));
    }

    [Fact]
    public void Class2DFreshRunLogParsingIsUnchanged()
    {
        var job = Connect(new Class2D { Algorithm = Class2DAlgorithm.VDAM });
        var lines = new List<string> { "preamble" };
        for (int it = 1; it <= 11; it++)
        {
            lines.Add(" CurrentResolution= 10");
            lines.Add($"iteration {it}");
        }
        File.WriteAllText(Path.Combine(job.DirectoryPath, "run.out"), string.Join('\n', lines));

        job.TrackProgressLogs()?.Invoke();

        Assert.Equal(2, job.LogsAvailableIteration);
        Assert.Equal("preamble", File.ReadAllText(job.LogFilePath(0)).Trim());
        Assert.Contains("iteration 10", File.ReadAllText(job.LogFilePath(1)));
        Assert.Contains("iteration 11", File.ReadAllText(job.LogFilePath(2)));
    }

    [Fact]
    public void InitialModelContinuesFromWrittenResultsWithoutRestartingDeNovo()
    {
        var job = Connect(new InitialReference { NIterations = 100, NClasses = 2, Symmetry = "D7", ApplySymmetryLater = true });
        string[] files = ["optimiser.star", "model.star", "data.star", "sampling.star", "class001.mrc"];
        WriteIteration(job, 30, files);
        WriteIteration(job, 37, files); // never a VDAM result iteration
        WriteIteration(job, 40, "optimiser.star", "model.star"); // killed while writing

        Assert.Equal(30, job.FindContinuableIteration());
        job.BeginContinuation();
        Assert.Equal(4, job.ContinuingFromIteration);

        var args = job.ComposeCommandArguments();
        Assert.Equal("12/run_it030_optimiser.star", args["continue"]);
        Assert.Equal("12/./run", args["o"]);
        // Gradient mode, de novo reference, classes and symmetry come from the optimiser file.
        foreach (string key in new[] { "i", "ios", "grad", "denovo_3dref", "K", "sym", "iter", "pad", "healpix_order" })
            Assert.False(args.ContainsKey(key), key);

        // stdout starts at RELION iteration 31; logical iteration 5 starts at RELION iteration 41.
        var lines = new List<string> { "preamble" };
        for (int it = 31; it <= 42; it++)
        {
            lines.Add(" CurrentResolution= 10");
            lines.Add($"iteration {it}");
        }
        File.WriteAllText(job.PathStdOut, string.Join('\n', lines));

        job.TrackProgressLogs()?.Invoke();

        Assert.Equal(5, job.LogsAvailableIteration);
        Assert.Contains("iteration 31", File.ReadAllText(job.LogFilePath(4)));
        Assert.DoesNotContain("iteration 41", File.ReadAllText(job.LogFilePath(4)));
        Assert.Contains("iteration 41", File.ReadAllText(job.LogFilePath(5)));
    }

    [Fact]
    public void InitialModelRefusesOnceAllIterationsAreWritten()
    {
        var job = Connect(new InitialReference { NIterations = 100 });
        WriteIteration(job, 100, Complete);

        Assert.Contains("Finalize", job.WhyCannotContinue());
    }

    [Fact]
    public void JobTypesWithoutSupportExplainWhy()
    {
        var job = Connect(new Class3D());
        Assert.True(job.SupportsContinuation);
        Assert.False(new Refund.Jobs.Refinement.PostProcess.PostProcess3D.PostProcess().SupportsContinuation);
        Assert.NotNull(new Refund.Jobs.Refinement.PostProcess.PostProcess3D.PostProcess().WhyCannotContinue());
    }
}
