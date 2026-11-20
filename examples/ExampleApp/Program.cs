using ExampleApp;

Console.WriteLine("=== ProgramFlowTracer ExampleApp ===");
Console.WriteLine("Run this normally with 'dotnet run' to see it behave like any console app.");
Console.WriteLine("Instrument + run it with ProgramFlowTracer to see a full execution trace under .flowtrace/.");
Console.WriteLine();

var orderService = new OrderService();

var order1 = orderService.ProcessOrder(1001, "Ada Lovelace", quantity: 3);
Console.WriteLine($"Order 1001 total: {order1.Total:C}");

var order2 = await orderService.ProcessOrderAsync(1002, "Alan Turing", quantity: 1);
Console.WriteLine($"Order 1002 total: {order2.Total:C}");

try
{
    orderService.ProcessOrder(-1, "Invalid Customer", quantity: 1);
}
catch (ArgumentException ex)
{
    Console.WriteLine($"Expected validation failure: {ex.Message}");
}

var report = orderService.BuildDailyReport(new[] { order1, order2 });
Console.WriteLine($"Daily report: {report.OrderCount} orders, {report.TotalRevenue:C} revenue");

if (orderService.TryLookupDiscount("GOLD", out var discount))
{
    Console.WriteLine($"GOLD discount: {discount:P0}");
}

Console.WriteLine();
Console.WriteLine("Done.");
