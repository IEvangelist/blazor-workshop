﻿using BlazingPizza.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BlazingPizza.Server.Services
{
    public sealed class OrderStatusService : BackgroundService
    {
        private readonly IBackgroundOrderQueue _taskQueue;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OrderStatusService> _logger;

        public OrderStatusService(
            IBackgroundOrderQueue taskQueue,
            IServiceProvider serviceProvider,
            ILogger<OrderStatusService> logger) =>
            (_taskQueue, _serviceProvider, _logger) = (taskQueue, serviceProvider, logger);

        [SuppressMessage(
            "Style",
            "IDE0063:Use simple 'using' statement",
            Justification = "We need explicit scoping of the provider")]
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var workItem = await _taskQueue.DequeueAsync(stoppingToken);
                    var order = await workItem(stoppingToken);

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var hubContext =
                            scope.ServiceProvider
                                .GetRequiredService<IHubContext<OrderStatusHub, IOrderStatusHub>>();

                        var trackingOrderId = $"{order.OrderId}:{order.UserId}";
                        var orderWithStatus = await GetOrderAsync(scope.ServiceProvider, order.OrderId);

                        while (!orderWithStatus.IsDelivered)
                        {
                            await hubContext.Clients.Group(trackingOrderId).OrderStatusChanged(orderWithStatus);
                            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

                            orderWithStatus = OrderWithStatus.FromOrder(orderWithStatus.Order);
                        }

                        // Send final delivery status update.
                        await hubContext.Clients.Group(trackingOrderId).OrderStatusChanged(orderWithStatus);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Prevent throwing if stoppingToken was signaled
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred executing task work item.");
                }
            }
        }

        static async Task<OrderWithStatus> GetOrderAsync(IServiceProvider serviceProvider, int orderId)
        {
            var pizzeStoreContext =
                serviceProvider.GetRequiredService<PizzaStoreContext>();

            var order = await pizzeStoreContext.Orders
                .Where(o => o.OrderId == orderId)
                .Include(o => o.DeliveryLocation)
                .Include(o => o.Pizzas).ThenInclude(p => p.Special)
                .Include(o => o.Pizzas).ThenInclude(p => p.Toppings).ThenInclude(t => t.Topping)
                .SingleOrDefaultAsync();

            return OrderWithStatus.FromOrder(order);
        }
    }
}
