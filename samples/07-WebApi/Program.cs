using Emissary;
using Emissary.AspNetCore;
using WebApi;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddEmissary(options =>
{
    options.SystemPrompt = "You are a store support agent. Look up orders before refunding.";
    options.Tools.Add(SupportTools.LookupOrderTool);
    options.Tools.Add(SupportTools.RefundPaymentTool);
    options.Rules.Require("refund_payment", "lookup_order");
    // The human-in-the-loop gate: refunds suspend the run until the approval webhook fires.
    options.ApprovalRequired = tool => tool.Privileged;
});
builder.Services.AddSingleton<IAgentStateStore, InMemoryAgentStateStore>();

var app = builder.Build();

app.MapEmissaryAgent("/agent");
app.MapEmissaryApprovals("/agent/approvals");

app.Run();

namespace WebApi
{
    internal static partial class SupportTools
    {
        /// <summary>Looks up an order.</summary>
        /// <param name="orderId">The order id.</param>
        [ClaudeTool]
        public static string LookupOrder(string orderId) =>
            $"order {orderId}: 1x Widget, $39.99, delivered 2026-08-20";

        /// <summary>Refunds a payment for an order.</summary>
        /// <param name="orderId">The order id.</param>
        /// <param name="amount">The amount to refund.</param>
        [ClaudeTool(Privileged = true)]
        public static string RefundPayment(string orderId, double amount) =>
            $"refunded ${amount} for order {orderId}";
    }
}
