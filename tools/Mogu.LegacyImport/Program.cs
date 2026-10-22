using Mogu.LegacyImport;

ImportOptions? options;
try
{
    options = ImportOptions.Parse(args);
}
catch (FormatException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(ImportOptions.Usage);
    return 2;
}

if (options is null)
{
    Console.WriteLine(ImportOptions.Usage);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    var report = await LegacyImporter.RunAsync(options, Console.Out, cancellation.Token);
    report.Write(Console.Out);
    Console.WriteLine("Import completed.");
    return 0;
}
#pragma warning disable CA1031 // Top-level handler: report any failure and exit non-zero.
catch (Exception e)
#pragma warning restore CA1031
{
    var expected = e is InvalidOperationException or InvalidDataException or IOException or OperationCanceledException;
    Console.Error.WriteLine($"Import FAILED: {(expected ? e.Message : e.ToString())}");
    return 1;
}
