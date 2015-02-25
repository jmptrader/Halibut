using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Halibut.Transport;
using Halibut.Transport.Protocol;

namespace Halibut
{
    public class PollingClientCollection
    {
        readonly List<IPollingClient> pollingClients = new List<IPollingClient>();
        readonly object sync = new object();

        public void Add(PollingClient pollingClient)
        {
            lock (sync)
            {
                pollingClients.Add(pollingClient);
            }

            pollingClient.Start();
        }

        public void Dispose()
        {
            lock (sync)
            {
                foreach (var worker in pollingClients)
                {
                    worker.Dispose();
                }

                pollingClients.Clear();
            }
        }
    }

    public class HalibutRuntime : IDisposable
    {
        readonly ConcurrentDictionary<Uri, PendingRequestQueue> queues = new ConcurrentDictionary<Uri, PendingRequestQueue>();
        readonly X509Certificate2 serverCertficiate;
        readonly List<SecureListener> listeners = new List<SecureListener>();
        readonly HashSet<string> trustedThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase); 
        readonly ConcurrentDictionary<Uri, ServiceEndPoint> routeTable = new ConcurrentDictionary<Uri, ServiceEndPoint>();
        readonly ServiceInvoker invoker;
        readonly LogFactory logs = new LogFactory();
        readonly SecureClientConnectionPool pool = new SecureClientConnectionPool();
        readonly PollingClientCollection pollingClients = new PollingClientCollection();
        
        public HalibutRuntime(X509Certificate2 serverCertficiate) : this(new NullServiceFactory(), serverCertficiate)
        {
        }

        public HalibutRuntime(IServiceFactory serviceFactory, X509Certificate2 serverCertficiate)
        {
            this.serverCertficiate = serverCertficiate;
            invoker = new ServiceInvoker(serviceFactory);
        }

        public LogFactory Logs
        {
            get { return logs; }
        }

        PendingRequestQueue GetQueue(Uri target)
        {
            return queues.GetOrAdd(target, u => new PendingRequestQueue());
        }

        public int Listen()
        {
            return Listen(0);
        }

        public int Listen(int port)
        {
            return Listen(new IPEndPoint(IPAddress.Any, port));
        }

        public int Listen(IPEndPoint endpoint)
        {
            var listener = new SecureListener(endpoint, serverCertficiate, ListenerHandler, VerifyThumbprintOfIncomingClient, logs);
            listeners.Add(listener);
            return listener.Start();
        }

        void ListenerHandler(MessageExchangeProtocol obj)
        {
            obj.ExchangeAsServer(
                HandleIncomingRequest,
                id => GetQueue(id.SubscriptionId));
        }

        public void Subscription(ServiceEndPoint endPoint)
        {
            queues.AddOrUpdate(endPoint.BaseUri, u => new PendingRequestQueue(), (u, q) => q);
        }

        public void Poll(Uri subscription, ServiceEndPoint endPoint)
        {
            var client = new SecureClient(endPoint, serverCertficiate, logs.ForEndpoint(endPoint.ToString()), pool);
            pollingClients.Add(new PollingClient(subscription, client, HandleIncomingRequest));
        }

        public ServiceEndPoint Discover(Uri uri)
        {
            var client = new DiscoveryClient();
            return client.Discover(uri);
        }

        public TService CreateClient<TService>(string endpointBaseUri, string publicThumbprint)
        {
            return CreateClient<TService>(new ServiceEndPoint(endpointBaseUri, publicThumbprint));
        }

        public TService CreateClient<TService>(ServiceEndPoint endpoint)
        {
            return (TService)new HalibutProxy(SendOutgoingRequest, typeof(TService), endpoint).GetTransparentProxy();
        }

        ResponseMessage SendOutgoingRequest(RequestMessage request)
        {
            var endPoint = request.Destination;

            ServiceEndPoint routerEndPoint;
            if (routeTable.TryGetValue(endPoint.BaseUri, out routerEndPoint))
            {
                endPoint = routerEndPoint;
                request = new RequestMessage {ActivityId = request.ActivityId, Id = request.Id, Params = new[] {request}, Destination = endPoint, ServiceName = "Router", MethodName = "Route"};
            }

            switch (endPoint.BaseUri.Scheme.ToLowerInvariant())
            {
                case "https":
                    return SendOutgoingHttpsRequest(request);
                case "poll":
                    return SendOutgoingPollingRequest(request);
                default: throw new ArgumentException("Unknown endpoint type: " + endPoint.BaseUri.Scheme);
            }
        }

        ResponseMessage SendOutgoingHttpsRequest(RequestMessage request)
        {
            var client = new SecureClient(request.Destination, serverCertficiate, logs.ForEndpoint(request.Destination.ToString()), pool);

            ResponseMessage response = null;
            client.ExecuteTransaction(protocol =>
            {
                response = protocol.ExchangeAsClient(request);
            });
            return response;
        }

        ResponseMessage SendOutgoingPollingRequest(RequestMessage request)
        {
            var queue = queues.GetOrAdd(request.Destination.BaseUri, u => new PendingRequestQueue());
            return queue.QueueAndWait(request);
        }

        ResponseMessage HandleIncomingRequest(RequestMessage request)
        {
            // Is this message intended for /route? If so, unwrap the original message. 
            // If we have a route table entry for the original, then route it using SendOutgoingRequest again. Otherwise, 
            // pass it to the invoker since it must be intended for us.
            if (request.ServiceName == "Router")
            {
                var original = (RequestMessage) request.Params[0];

                ServiceEndPoint route;
                if (routeTable.TryGetValue(original.Destination.BaseUri, out route))
                {
                    // Needs to be routed again
                    return SendOutgoingRequest(original);
                }

                request = original;
            }

            return invoker.Invoke(request);
        }

        public void Trust(string clientThumbprint)
        {
            trustedThumbprints.Add(clientThumbprint);
        }

        bool VerifyThumbprintOfIncomingClient(string remoteThumbprint)
        {
            return trustedThumbprints.Contains(remoteThumbprint);
        }

        public void Route(ServiceEndPoint to, ServiceEndPoint via)
        {
            routeTable.TryAdd(to.BaseUri, via);
        }

        public void Dispose()
        {
            pollingClients.Dispose();
            pool.Dispose();
            foreach (var listener in listeners)
            {
                listener.Dispose();
            }
        }
    }
}