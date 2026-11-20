namespace ExampleApp;
public sealed record OrderResult(int OrderId, string CustomerName, int Quantity, decimal Total);
public sealed record DailyReport(int OrderCount, decimal TotalRevenue);
/// <summary>A small, deliberately ordinary service class - the kind of code ProgramFlowTracer is
/// meant to instrument. Nothing in here knows or cares that tracing exists.</summary>
public sealed class OrderService
{
    private const decimal UnitPrice = 19.99m;
    private static readonly Dictionary<string, decimal> Discounts = new()
    {
        ["GOLD"] = 0.15m,
        ["SILVER"] = 0.08m
    };
    public OrderResult ProcessOrder(int orderId, string customerName, int quantity)
    {
        var __ftCall_0a076332_0 = global::ProgramFlowTracer.Runtime.FlowTracer.Enter("ProcessOrder", "ExampleApp.OrderService", @"C:\Users\Admin\source\repos\andikatjacobdennis\ProgramFlowTracer\examples\ExampleApp\OrderService.cs", 19, 24, new global::ProgramFlowTracer.Runtime.FlowTraceParameter[] { new global::ProgramFlowTracer.Runtime.FlowTraceParameter("orderId", typeof(int), orderId, false), new global::ProgramFlowTracer.Runtime.FlowTraceParameter("customerName", typeof(string), customerName, false), new global::ProgramFlowTracer.Runtime.FlowTraceParameter("quantity", typeof(int), quantity, false) });
        try
        {
            ValidateOrder(orderId, customerName, quantity);
            var total = CalculateTotal(quantity);
            {
                global::ExampleApp.OrderResult __ftRet_0a076332_1 = new OrderResult(orderId, customerName, quantity, total);
                global::ProgramFlowTracer.Runtime.FlowTracer.Exit(__ftCall_0a076332_0, __ftRet_0a076332_1, typeof(global::ExampleApp.OrderResult), null);
                return __ftRet_0a076332_1;
            }
        }
        catch (global::System.Exception __ftEx_0a076332_2)
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Exception(__ftCall_0a076332_0, __ftEx_0a076332_2);
            throw;
        }
        finally
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Leave(__ftCall_0a076332_0);
        }
    }

    public async Task<OrderResult> ProcessOrderAsync(int orderId, string customerName, int quantity)
    {
        var __ftCall_0a076332_3 = global::ProgramFlowTracer.Runtime.FlowTracer.Enter("ProcessOrderAsync", "ExampleApp.OrderService", @"C:\Users\Admin\source\repos\andikatjacobdennis\ProgramFlowTracer\examples\ExampleApp\OrderService.cs", 26, 36, new global::ProgramFlowTracer.Runtime.FlowTraceParameter[] { new global::ProgramFlowTracer.Runtime.FlowTraceParameter("orderId", typeof(int), orderId, false), new global::ProgramFlowTracer.Runtime.FlowTraceParameter("customerName", typeof(string), customerName, false), new global::ProgramFlowTracer.Runtime.FlowTraceParameter("quantity", typeof(int), quantity, false) });
        try
        {
            ValidateOrder(orderId, customerName, quantity);
            await Task.Delay(10);
            var total = CalculateTotal(quantity);
            {
                global::ExampleApp.OrderResult __ftRet_0a076332_4 = new OrderResult(orderId, customerName, quantity, total);
                global::ProgramFlowTracer.Runtime.FlowTracer.Exit(__ftCall_0a076332_3, __ftRet_0a076332_4, typeof(global::ExampleApp.OrderResult), null);
                return __ftRet_0a076332_4;
            }
        }
        catch (global::System.Exception __ftEx_0a076332_5)
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Exception(__ftCall_0a076332_3, __ftEx_0a076332_5);
            throw;
        }
        finally
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Leave(__ftCall_0a076332_3);
        }
    }

    public DailyReport BuildDailyReport(IReadOnlyList<OrderResult> orders)
    {
        var __ftCall_0a076332_9 = global::ProgramFlowTracer.Runtime.FlowTracer.Enter("BuildDailyReport", "ExampleApp.OrderService", @"C:\Users\Admin\source\repos\andikatjacobdennis\ProgramFlowTracer\examples\ExampleApp\OrderService.cs", 34, 24, new global::ProgramFlowTracer.Runtime.FlowTraceParameter[] { new global::ProgramFlowTracer.Runtime.FlowTraceParameter("orders", typeof(global::System.Collections.Generic.IReadOnlyList<global::ExampleApp.OrderResult>), orders, false) });
        try
        {
            decimal SumTotals(IReadOnlyList<OrderResult> items)
            {
                var __ftCall_0a076332_6 = global::ProgramFlowTracer.Runtime.FlowTracer.Enter("SumTotals", "ExampleApp.OrderService+SumTotals", @"C:\Users\Admin\source\repos\andikatjacobdennis\ProgramFlowTracer\examples\ExampleApp\OrderService.cs", 36, 17, new global::ProgramFlowTracer.Runtime.FlowTraceParameter[] { new global::ProgramFlowTracer.Runtime.FlowTraceParameter("items", typeof(global::System.Collections.Generic.IReadOnlyList<global::ExampleApp.OrderResult>), items, false) });
                try
                {
                    decimal sum = 0;
                    foreach (var o in items)
                    {
                        sum += o.Total;
                    }

                    {
                        decimal __ftRet_0a076332_7 = sum;
                        global::ProgramFlowTracer.Runtime.FlowTracer.Exit(__ftCall_0a076332_6, __ftRet_0a076332_7, typeof(decimal), null);
                        return __ftRet_0a076332_7;
                    }
                }
                catch (global::System.Exception __ftEx_0a076332_8)
                {
                    global::ProgramFlowTracer.Runtime.FlowTracer.Exception(__ftCall_0a076332_6, __ftEx_0a076332_8);
                    throw;
                }
                finally
                {
                    global::ProgramFlowTracer.Runtime.FlowTracer.Leave(__ftCall_0a076332_6);
                }
            }

            {
                global::ExampleApp.DailyReport __ftRet_0a076332_10 = new DailyReport(orders.Count, SumTotals(orders));
                global::ProgramFlowTracer.Runtime.FlowTracer.Exit(__ftCall_0a076332_9, __ftRet_0a076332_10, typeof(global::ExampleApp.DailyReport), null);
                return __ftRet_0a076332_10;
            }
        }
        catch (global::System.Exception __ftEx_0a076332_11)
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Exception(__ftCall_0a076332_9, __ftEx_0a076332_11);
            throw;
        }
        finally
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Leave(__ftCall_0a076332_9);
        }
    }

    public bool TryLookupDiscount(string code, out decimal rate)
    {
        var __ftCall_0a076332_12 = global::ProgramFlowTracer.Runtime.FlowTracer.Enter("TryLookupDiscount", "ExampleApp.OrderService", @"C:\Users\Admin\source\repos\andikatjacobdennis\ProgramFlowTracer\examples\ExampleApp\OrderService.cs", 50, 17, new global::ProgramFlowTracer.Runtime.FlowTraceParameter[] { new global::ProgramFlowTracer.Runtime.FlowTraceParameter("code", typeof(string), code, false), global::ProgramFlowTracer.Runtime.FlowTraceParameter.Unavailable("rate", typeof(decimal)) });
        try
        {
            {
                bool __ftRet_0a076332_13 = Discounts.TryGetValue(code, out rate);
                global::ProgramFlowTracer.Runtime.FlowTracer.Exit(__ftCall_0a076332_12, __ftRet_0a076332_13, typeof(bool), new global::ProgramFlowTracer.Runtime.FlowTraceParameter[] { new global::ProgramFlowTracer.Runtime.FlowTraceParameter("rate", typeof(decimal), rate, false) });
                return __ftRet_0a076332_13;
            }
        }
        catch (global::System.Exception __ftEx_0a076332_14)
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Exception(__ftCall_0a076332_12, __ftEx_0a076332_14);
            throw;
        }
        finally
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Leave(__ftCall_0a076332_12);
        }
    }

    private static void ValidateOrder(int orderId, string customerName, int quantity)
    {
        var __ftCall_0a076332_15 = global::ProgramFlowTracer.Runtime.FlowTracer.Enter("ValidateOrder", "ExampleApp.OrderService", @"C:\Users\Admin\source\repos\andikatjacobdennis\ProgramFlowTracer\examples\ExampleApp\OrderService.cs", 52, 25, new global::ProgramFlowTracer.Runtime.FlowTraceParameter[] { new global::ProgramFlowTracer.Runtime.FlowTraceParameter("orderId", typeof(int), orderId, false), new global::ProgramFlowTracer.Runtime.FlowTraceParameter("customerName", typeof(string), customerName, false), new global::ProgramFlowTracer.Runtime.FlowTraceParameter("quantity", typeof(int), quantity, false) });
        try
        {
            if (orderId <= 0)
            {
                throw new ArgumentException("Order id must be positive.", nameof(orderId));
            }

            if (string.IsNullOrWhiteSpace(customerName))
            {
                throw new ArgumentException("Customer name is required.", nameof(customerName));
            }

            if (quantity <= 0)
            {
                throw new ArgumentException("Quantity must be positive.", nameof(quantity));
            }

            global::ProgramFlowTracer.Runtime.FlowTracer.ExitVoid(__ftCall_0a076332_15, null);
        }
        catch (global::System.Exception __ftEx_0a076332_16)
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Exception(__ftCall_0a076332_15, __ftEx_0a076332_16);
            throw;
        }
        finally
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Leave(__ftCall_0a076332_15);
        }
    }

    private static decimal CalculateTotal(int quantity)
    {
        var __ftCall_0a076332_17 = global::ProgramFlowTracer.Runtime.FlowTracer.Enter("CalculateTotal", "ExampleApp.OrderService", @"C:\Users\Admin\source\repos\andikatjacobdennis\ProgramFlowTracer\examples\ExampleApp\OrderService.cs", 70, 28, new global::ProgramFlowTracer.Runtime.FlowTraceParameter[] { new global::ProgramFlowTracer.Runtime.FlowTraceParameter("quantity", typeof(int), quantity, false) });
        try
        {
            {
                decimal __ftRet_0a076332_18 = UnitPrice * quantity;
                global::ProgramFlowTracer.Runtime.FlowTracer.Exit(__ftCall_0a076332_17, __ftRet_0a076332_18, typeof(decimal), null);
                return __ftRet_0a076332_18;
            }
        }
        catch (global::System.Exception __ftEx_0a076332_19)
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Exception(__ftCall_0a076332_17, __ftEx_0a076332_19);
            throw;
        }
        finally
        {
            global::ProgramFlowTracer.Runtime.FlowTracer.Leave(__ftCall_0a076332_17);
        }
    }
}