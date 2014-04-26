﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Common.Logging;
using Couchbase.Configuration.Server.Serialization;
using Couchbase.Utils;
using Newtonsoft.Json;

namespace Couchbase.Configuration.Server.Providers.Streaming
{
    internal delegate void ConfigChanged(IBucketConfig streamingHttp);

    internal delegate void ErrorOccurred(IBucketConfig streamingHttp);

    internal class ConfigThreadState 
    {
        private readonly ILog _log = LogManager.GetCurrentClassLogger();
        private readonly BucketConfig _bucketConfig;
        private readonly ConfigChanged _configChangedDelegate;
        private readonly ErrorOccurred _errorOccurredDelegate;
        private CancellationToken _cancellationToken;

        public ConfigThreadState(BucketConfig bucketConfig, ConfigChanged configChangedDelegate,
            ErrorOccurred errorOccurredDelegate, CancellationToken cancellationToken)
        {
            _bucketConfig = bucketConfig;
            _configChangedDelegate += configChangedDelegate;
            _errorOccurredDelegate += errorOccurredDelegate;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        ///     This is to support $HOST variable in the URI in _some_ cases
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        private string GetSurrogateHost(Uri uri)
        {
            return uri.Host;
        }

        public void ListenForConfigChanges()
        {
            var count = 0;

            //Make a copy of the nodes and shuffle them for randomness
            var nodes = _bucketConfig.Nodes.ToList();

            //This will keep trying until it runs out of servers to try in the cluster
            while (nodes.ToList().Any())
            {
                try
                {
                    nodes = nodes.Shuffle();
                    var node = nodes[0];
                    nodes.Remove(node);

                    var streamingUri = _bucketConfig.GetTerseStreamingUri(node);
                    _log.Info(m=>m("Listening to {0}", streamingUri));

                    using (var webClient = new AuthenticatingWebClient(_bucketConfig.Name, _bucketConfig.Password))
                    using (var stream = webClient.OpenRead(streamingUri))
                    {
                        //this will cancel the infinite wait below - the temp variable removes chance of deadlock when dispose is called on the closure
                        var temp = webClient;
                        _cancellationToken.Register(temp.CancelAsync);

                        if (stream == null) return;
                        stream.ReadTimeout = Timeout.Infinite;
                        using (var reader = new StreamReader(stream, Encoding.UTF8, false))
                        {
                            string config;
                            while ((config = reader.ReadLine()) != null)
                            {
                                if (config != String.Empty)
                                {
                                    _log.Info(m=>m("configuration changed count: {0}", count++));
                                    _log.Info(m=>m("Worker Thread: {0}", Thread.CurrentThread.ManagedThreadId));
                                    _log.Debug(m=>m("{0}", config));

                                    var bucketConfig = JsonConvert.DeserializeObject<BucketConfig>(config);
                                    bucketConfig.SurrogateHost = GetSurrogateHost(streamingUri);
                                    if (_configChangedDelegate != null)
                                    {
                                        _configChangedDelegate(bucketConfig);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (WebException e)
                {
                    _log.Error(e);
                }
                catch (IOException e)
                {
                    _log.Error(e);
                }
            }

            //We tried all nodes in the current configuration, alert the provider that we need to try to 
            //re-bootstrap from the beginning
            if (nodes.Count == 0)
            {
                _errorOccurredDelegate(_bucketConfig);
            }
        }
    }
}