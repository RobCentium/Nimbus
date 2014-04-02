﻿using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using Nimbus.Extensions;
using Nimbus.HandlerFactories;
using Nimbus.Handlers;
using Nimbus.MessageContracts;

namespace Nimbus.Infrastructure.RequestResponse
{
    internal class RequestMessageDispatcher : IMessageDispatcher
    {
        private readonly INimbusMessagingFactory _messagingFactory;
        private readonly IBrokeredMessageFactory _brokeredMessageFactory;
        private readonly Type _messageType;
        private readonly IRequestHandlerFactory _requestHandlerFactory;
        private readonly IClock _clock;
        private readonly ILogger _logger;

        public RequestMessageDispatcher(
            INimbusMessagingFactory messagingFactory,
            IBrokeredMessageFactory brokeredMessageFactory,
            Type messageType,
            IRequestHandlerFactory requestHandlerFactory,
            IClock clock,
            ILogger logger)
        {
            _messagingFactory = messagingFactory;
            _brokeredMessageFactory = brokeredMessageFactory;
            _messageType = messageType;
            _requestHandlerFactory = requestHandlerFactory;
            _clock = clock;
            _logger = logger;
        }

        public async Task Dispatch(BrokeredMessage message)
        {
            var request = message.GetBody(_messageType);
            var dispatchMethod = GetGenericDispatchMethodFor(request);
            await (Task) dispatchMethod.Invoke(this, new[] {request, message});
        }

        private async Task Dispatch<TBusRequest, TBusResponse>(TBusRequest busRequest, BrokeredMessage message)
            where TBusRequest : IBusRequest<TBusRequest, TBusResponse>
            where TBusResponse : IBusResponse
        {
            var replyQueueName = message.ReplyTo;
            var replyQueueClient = _messagingFactory.GetQueueSender(replyQueueName);

            BrokeredMessage responseMessage;
            try
            {
                using (var handler = _requestHandlerFactory.GetHandler<TBusRequest, TBusResponse>())
                {
                    var handlerTask = handler.Component.Handle(busRequest);
                    var wrapperTask = new LongLivedTaskWrapper<TBusResponse>(handlerTask, handler.Component as ILongRunningHandler, message, _clock);
                    var response = await wrapperTask.AwaitCompletion();
                    responseMessage = _brokeredMessageFactory.CreateSuccessfulResponse(response, message);
                    LogActivity("Sending successful response message", responseMessage, replyQueueName);
                }
            }
            catch (Exception exc)
            {
                responseMessage = _brokeredMessageFactory.CreateFailedResponse(message, exc);
                LogActivity("Sending failed response message", responseMessage, replyQueueName);
            }

            await replyQueueClient.Send(responseMessage);
            LogActivity("Sent response message", responseMessage, replyQueueName);
        }

        private void LogActivity(string activity, BrokeredMessage message, string path)
        {
            _logger.Debug("{0} {1} to {2} [MessageId:{3}, CorrelationId:{4}]",
                activity, message.SafelyGetBodyTypeNameOrDefault(), path, message.MessageId, message.CorrelationId);
        }

        internal static MethodInfo GetGenericDispatchMethodFor(object request)
        {
            var closedGenericTypeOfIBusRequest = request.GetType()
                                                        .GetInterfaces()
                                                        .Where(t => t.IsClosedTypeOf(typeof (IBusRequest<,>)))
                                                        .Single();

            var genericArguments = closedGenericTypeOfIBusRequest.GetGenericArguments();
            var requestType = genericArguments[0];
            var responseType = genericArguments[1];

            var openGenericMethod = typeof (RequestMessageDispatcher).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Instance);
            var closedGenericMethod = openGenericMethod.MakeGenericMethod(new[] {requestType, responseType});
            return closedGenericMethod;
        }
    }
}