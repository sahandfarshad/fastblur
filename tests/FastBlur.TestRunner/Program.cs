// Dev-only in-process xUnit runner.
//
// `dotnet test` needs vstest's testhost, which talks to vstest.console over
// loopback TCP. In sandboxes where a transparent egress proxy intercepts
// .NET loopback TCP (the connection reaches the proxy, never the listener),
// `dotnet test` aborts with "failed to connect to testhost process after
// N seconds" even though the tests themselves are fine. This runner executes
// the same test assembly in-process with xunit.runner.utility, no sockets,
// no testhost — so the suite can actually be run (and proven green) on
// such a machine. CI still uses `dotnet test` (see .github/workflows/ci.yml).
//
// Usage:
//   dotnet run --project tests/FastBlur.TestRunner -c Release
// Exit code is 0 when every executed test passes, 1 otherwise.

using Xunit.Runners;

var testAssembly = typeof(FastBlur.Core.Tests.ClassifierTests).Assembly.Location;

var passed = 0;
var failed = 0;
var skipped = 0;
var failures = new List<string>();
var done = new ManualResetEventSlim(false);

var runner = AssemblyRunner.WithoutAppDomain(testAssembly);
runner.OnExecutionComplete += info =>
{
    passed = info.TotalTests - info.TestsFailed - info.TestsSkipped;
    failed = info.TestsFailed;
    skipped = info.TestsSkipped;
    Console.WriteLine();
    Console.WriteLine($"Tests run: {info.TotalTests}, Passed: {passed}, Failed: {failed}, Skipped: {skipped}, " +
                      $"Elapsed: {info.ExecutionTime:0.00}s");
    done.Set();
};
runner.OnTestFailed += info =>
{
    failures.Add($"{info.TestDisplayName}: {info.ExceptionMessage}");
    Console.WriteLine($"FAIL: {info.TestDisplayName}");
    Console.WriteLine($"      {info.ExceptionMessage}");
    if (info.ExceptionStackTrace is not null)
    {
        foreach (var line in info.ExceptionStackTrace.Split('\n').Take(3))
            Console.WriteLine($"      {line.TrimEnd()}");
    }
};
runner.OnTestSkipped += info =>
{
    Console.WriteLine($"SKIP: {info.TestDisplayName}");
};
runner.OnErrorMessage += info =>
{
    Console.WriteLine($"RUNNER ERROR: {info.ExceptionMessage}");
};

Console.WriteLine($"Running tests in {Path.GetFileName(testAssembly)} ...");
runner.Start();

if (!done.Wait(TimeSpan.FromMinutes(10)))
{
    Console.WriteLine("ERROR: test run did not finish within 10 minutes.");
    return 2;
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failed tests:");
    foreach (var f in failures) Console.WriteLine(" - " + f);
}

var exitCode = failed == 0 ? 0 : 1;
try { runner.Dispose(); } catch (InvalidOperationException) { /* runner may still be draining; results already printed */ }
return exitCode;
