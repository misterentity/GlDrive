using GlDrive.Mcp;
using ModelContextProtocol.Server;

// stdout belongs exclusively to the SDK's MCP transport.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
try
{
    using var api = new ControlApiClient();
    var options = new McpBridge(api).CreateOptions();
    await using var server = McpServer.Create(new StdioServerTransport(options), options);
    await server.RunAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
catch (Exception)
{
    // Exception details can contain tool inputs or credentials. Keep process-level errors minimal.
    await Console.Error.WriteLineAsync("GlDrive MCP bridge stopped unexpectedly. Restart the MCP connection.");
    return 1;
}
