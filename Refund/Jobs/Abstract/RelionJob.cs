using System.Globalization;
using System.Text.RegularExpressions;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.UIFields;
using Refund.Utils;

namespace Refund.Jobs;

/// <summary>
/// Base class for all jobs that utilize the RELION software for cryo-EM data processing.
/// RELION (REgularised LIkelihood OptimisatioN) is a widely used software package for
/// high-resolution refinement of single-particle electron cryo-microscopy data.
/// </summary>
/// <remarks>
/// This class extends the base Job class with RELION-specific configuration:
/// - Adds "relion" to the required modules list
/// - Adds both "relion" and "mpi" to the supported modules list
/// - Adds a touch command suffix to create a success marker file that RELION expects
/// - Continues stopped relion_refine runs in place (see the Continuation region)
/// </remarks>
[GenerateReadOnly]
public abstract class RelionJob : Job
{
    /// <summary>
    /// Gets the modules that this job can utilize if available.
    /// Includes the base modules plus "relion" for processing and "mpi" for parallelization.
    /// </summary>
    public override string[] SupportedModules => base.RequiredModules.Concat(["relion", "mpi"]).ToArray();

    /// <summary>
    /// Gets the modules that must be available for this job to run.
    /// Includes the base required modules plus "relion".
    /// </summary>
    public override string[] RequiredModules => base.RequiredModules.Concat(["relion"]).ToArray();

    /// <summary>
    /// Gets a command suffix that creates a success marker file when the job completes successfully.
    /// RELION expects this file to exist to indicate normal termination of the job.
    /// </summary>
    public override string CommandSuffix => $" && touch {PathSuccess}";

    #region Continuation

    // relion_refine jobs (classification, refinement) can continue from any iteration's optimiser file with
    // --continue, e.g. after hitting a cluster time limit. Relay continues them in place: the same directory and
    // output root, so run_itNNN_* paths, logs and figures simply carry on from the next iteration.

    /// <summary>
    /// Logical iteration at which the current run's stdout starts: 0 for a run from scratch, otherwise the first
    /// iteration after the one being continued. Log parsing numbers iterations from here.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public int ContinuingFromIteration { get; set; } = 0;

    /// <summary>
    /// RELION iteration whose run_itNNN_optimiser.star the current run continues in place, or -1 for a run that
    /// doesn't continue this job's own files.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public int ContinuedFromRelionIteration { get; set; } = -1;

    public override bool IsContinuingRun => SupportsContinuation && ContinuedFromRelionIteration >= 0;

    /// <summary>
    /// How many RELION iterations one logical (displayed) iteration spans. VDAM writes results only every N
    /// iterations, and only those can be continued from.
    /// </summary>
    protected virtual int RelionIterationsPerResult => 1;

    /// <summary>Total RELION iterations the run is configured for, or null if it stops on convergence.</summary>
    protected virtual int? TotalRelionIterations => null;

    /// <summary>
    /// Files relion_refine --continue reads for an iteration. MlOptimiser::write writes the optimiser first, then
    /// model, data and sampling, without temporary files; requiring all of them means the iteration was written
    /// completely before the run was killed.
    /// </summary>
    protected virtual IEnumerable<string> ContinuationFiles(int relionIteration) =>
    [
        RelionIterationFile(relionIteration, "optimiser.star"),
        RelionIterationFile(relionIteration, "model.star"),
        RelionIterationFile(relionIteration, "data.star"),
        RelionIterationFile(relionIteration, "sampling.star"),
    ];

    /// <summary>Path of a per-iteration RELION output file in this job's directory.</summary>
    protected string RelionIterationFile(int relionIteration, string suffix) =>
        Path.Combine(DirectoryPath, $"run_it{relionIteration:D3}_{suffix}");

    /// <summary>The optimiser file the current continuation reads.</summary>
    public string ContinuationOptimiserPath => RelionIterationFile(ContinuedFromRelionIteration, "optimiser.star");

    private static readonly Regex OptimiserFileRegex = new(@"^run_it(\d{3,})_optimiser\.star$", RegexOptions.Compiled);

    /// <summary>
    /// The latest completely written RELION iteration (at least 1) that a continuation can start from, or -1.
    /// </summary>
    public int FindContinuableIteration()
    {
        if (Space == null || Id <= 0 || !Directory.Exists(DirectoryPath))
            return -1;

        int perResult = Math.Max(1, RelionIterationsPerResult);
        var candidates = Directory.EnumerateFiles(DirectoryPath, "run_it*_optimiser.star")
                                  .Select(path => OptimiserFileRegex.Match(Path.GetFileName(path)))
                                  .Where(match => match.Success)
                                  .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                                  .Where(iteration => iteration >= 1 && iteration % perResult == 0)
                                  .OrderByDescending(iteration => iteration);

        foreach (int iteration in candidates)
            if (ContinuationFiles(iteration).All(path => File.Exists(path) && new FileInfo(path).Length > 0))
                return iteration;

        return -1;
    }

    public override string WhyCannotContinue()
    {
        if (!SupportsContinuation)
            return base.WhyCannotContinue();
        if (Status is not (JobStatus.Failed or JobStatus.Aborted or JobStatus.Interrupted))
            return "Only failed, aborted or interrupted jobs can be continued.";

        int iteration = FindContinuableIteration();
        if (iteration < 0)
            return "No iteration was written completely, so there is nothing to continue from. Clear and rerun the job instead.";
        if (TotalRelionIterations is { } total && iteration >= total)
            return $"All {total} iterations were already written. Finalize the job instead.";

        return null;
    }

    public override void BeginContinuation()
    {
        if (WhyCannotContinue() is { } reason)
            throw new InvalidOperationException(reason);

        int relionIteration = FindContinuableIteration();
        int logical = relionIteration / Math.Max(1, RelionIterationsPerResult);

        ContinuedFromRelionIteration = relionIteration;
        ContinuingFromIteration = logical + 1;
        // Anything past the continued iteration is overwritten (or removed while staging) by the new run.
        VisAvailableIteration = Math.Min(VisAvailableIteration, logical);
        LogsAvailableIteration = Math.Min(LogsAvailableIteration, logical);
        HasRunToCompletion = false;
    }

    public override void EndContinuation()
    {
        ContinuedFromRelionIteration = -1;
        ContinuingFromIteration = 0;
    }

    private static readonly Regex RelionIterationFileRegex = new(@"^run_(?:ct\d+_)?it(\d{3,})_", RegexOptions.Compiled);

    private static readonly Regex RelayIterationFileRegex = new(@"(?:^|_)it(\d{4})(?:[_.]|$)|^card_(\d{4})\.", RegexOptions.Compiled);

    protected override void StageContinuation()
    {
        int relionIteration = ContinuedFromRelionIteration;
        int logical = relionIteration / Math.Max(1, RelionIterationsPerResult);

        if (!File.Exists(ContinuationOptimiserPath))
            throw new FileNotFoundException("The optimiser file to continue from no longer exists.", ContinuationOptimiserPath);

        // Partial output of the iteration that was interrupted (and of any later ones) would otherwise be mistaken
        // for results until the new run overwrites it.
        foreach (string path in Directory.EnumerateFiles(DirectoryPath))
        {
            var match = RelionIterationFileRegex.Match(Path.GetFileName(path));
            if (match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) > relionIteration)
                File.Delete(path);
        }

        // Figures and logs are regenerated only when missing, so stale ones must go.
        if (Directory.Exists(RelayResultsDirectoryPath))
            foreach (string path in Directory.EnumerateFiles(RelayResultsDirectoryPath))
            {
                var match = RelayIterationFileRegex.Match(Path.GetFileName(path));
                if (!match.Success)
                    continue;
                string digits = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (int.Parse(digits, CultureInfo.InvariantCulture) > logical)
                    File.Delete(path);
            }

        // Log parsing assumes stdout starts with the continued run; keep the earlier output for reference.
        foreach (string name in new[] { NameStdOut, NameStdErr, "run.out", "run.err" }.Distinct())
        {
            string path = Path.Combine(DirectoryPath, name);
            if (File.Exists(path))
                File.Move(path, $"{path}.until_it{relionIteration:D3}", overwrite: true);
        }

        // Exit markers of the stopped run (Relay's own, and RELION's from --pipeline_control). A leftover
        // RELION_JOB_ABORT_NOW would make the continuation abort right away.
        foreach (string name in new[] { NameSuccess, "RELION_JOB_EXIT_SUCCESS", "RELION_JOB_EXIT_FAILURE",
                                        "RELION_JOB_EXIT_ABORTED", "RELION_JOB_ABORT_NOW" })
        {
            string path = Path.Combine(DirectoryPath, name);
            if (File.Exists(path))
                File.Delete(path);
        }

        // Worker-pool coordination data belongs to the stopped run.
        string poolDirectory = Path.Combine(DirectoryPath, "pool");
        if (this is IPooledJob && Directory.Exists(poolDirectory))
            FileUtils.DeleteDirectoryWithRetry(poolDirectory);
    }

    /// <summary>
    /// Options relion_refine re-reads on --continue that Relay passes through from a run's normal arguments:
    /// compute settings that may legitimately differ between the stopped run and its continuation. Everything else
    /// (inputs, model, sampling, iterations) comes from the optimiser file, so the continuation resumes the same run.
    /// </summary>
    private static readonly HashSet<string> ContinuationPassThrough =
    [
        "j", "gpu", "scratch_dir", "keep_free_scratch", "reuse_scratch", "keep_scratch", "pool",
        "dont_combine_weights_via_disc", "no_parallel_disc_io", "preread_images", "free_gpu_memory",
        "pool_dir", "pool_batch", "grad_write_iter", "pipeline_control",
    ];

    /// <summary>Additional arguments that must not reach a continuation: Relay sets them, or --pad would reinitialise the model.</summary>
    private static readonly HashSet<string> ContinuationBlockedExtras = ["i", "ios", "ref", "o", "continue", "pad"];

    /// <summary>
    /// Turns the arguments of a run from scratch into those of its in-place continuation: --continue with the
    /// latest complete optimiser file, the same output root, the compute options, and the user's additional
    /// arguments.
    /// </summary>
    protected Dictionary<string, string> ToContinuationArguments(Dictionary<string, string> fresh, string additionalArguments)
    {
        var result = fresh.Where(kv => ContinuationPassThrough.Contains(kv.Key))
                          .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (!string.IsNullOrWhiteSpace(additionalArguments))
            foreach (var kv in ArgumentStringToDictionary(additionalArguments))
                if (!ContinuationBlockedExtras.Contains(kv.Key))
                    result[kv.Key] = kv.Value;

        // Pool-owned settings win over additional arguments, as in the normal composition.
        if (fresh.ContainsKey("pool_dir"))
            foreach (string key in new[] { "j", "pool_dir", "pool_batch" })
                if (fresh.TryGetValue(key, out var value))
                    result[key] = value;

        result["continue"] = Space.GetRelativePath(ContinuationOptimiserPath);
        result["o"] = ContinuationOutputRoot();
        return result;
    }

    private static readonly Regex OutputRootRegex = new(@"^\s*_rlnOutputRootName\s+(\S+)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// The --o value for an in-place continuation. RELION appends "_ct{iteration}" to the output root when --o
    /// equals the root stored in the optimiser file, which would rename every output. Spelling the same directory
    /// differently ("12/./run" vs "12/run") keeps the run_itNNN_* names.
    /// </summary>
    private string ContinuationOutputRoot()
    {
        string canonical = Space.GetRelativePath(Path.Combine(DirectoryPath, "run"));
        string alternative = Path.Combine(Path.GetDirectoryName(canonical) ?? "", ".", "run");

        string stored = null;
        try
        {
            var match = OutputRootRegex.Match(File.ReadAllText(ContinuationOptimiserPath));
            if (match.Success)
                stored = match.Groups[1].Value.Trim('"', '\'');
        }
        catch (IOException)
        {
        }

        // Without a readable root, assume the default spelling Relay uses for runs from scratch.
        return stored == null || stored == canonical ? alternative : canonical;
    }

    #endregion
}
