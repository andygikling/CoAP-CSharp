﻿/*
 * Copyright (c) 2011-2015, Longxiang He <helongxiang@smeshlink.com>,
 * SmeshLink Technology Co.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY.
 * 
 * This file is part of the CoAP.NET, a CoAP framework in C#.
 * Please see README for more information.
 */

using System;
#if NETSTANDARD1_3
using System.Threading;
#else
using System.Timers;
#endif
using Com.AugustCellars.CoAP.Log;
using Com.AugustCellars.CoAP.Net;
using Com.AugustCellars.CoAP.Observe;

namespace Com.AugustCellars.CoAP.Stack
{
    public class ObserveLayer : AbstractLayer
    {
        private static readonly ILogger log = LogManager.GetLogger(typeof(ObserveLayer));
        private static readonly Object ReregistrationContextKey = "ReregistrationContext";
        private static readonly Random _Random = new Random();

        /// <summary>
        /// Additional time to wait until re-registration
        /// </summary>
        private Int32 _backoff;

        /// <summary>
        /// Constructs a new observe layer.
        /// </summary>
        public ObserveLayer(ICoapConfig config)
        {
            _backoff = config.NotificationReregistrationBackoff;
        }

        /// <inheritdoc/>
        public override void SendResponse(INextLayer nextLayer, Exchange exchange, Response response)
        {
            ObserveRelation relation = exchange.Relation;
            if (relation != null && relation.Established)  {
                if (exchange.Request.IsAcknowledged || exchange.Request.Type == MessageType.NON) {
                    // Transmit errors as CON
                    if (!Code.IsSuccess(response.Code)) {
                        if (log.IsDebugEnabled) { 
                            log.Debug("Response has error code " + response.Code + " and must be sent as CON");
                        }
                        response.Type = MessageType.CON;
                        relation.Cancel();
                    }
                    else { 
                        // Make sure that every now and than a CON is mixed within
                        if (relation.Check()) {
                            if (log.IsDebugEnabled) {
                                log.Debug("The observe relation check requires the notification to be sent as CON");
                            }
                            response.Type = MessageType.CON;
                        }
                        else {
                            // By default use NON, but do not override resource decision
                            if (response.Type == MessageType.Unknown) {
                                response.Type = MessageType.NON;
                            }
                        }
                    }
                }

                // This is a notification
                response.Last = false;

                /*
                 * The matcher must be able to find the NON notifications to remove
                 * them from the exchangesByID map
                 */
                if (response.Type == MessageType.NON) {
                    relation.AddNotification(response);
                }

                /*
                 * Only one Confirmable message is allowed to be in transit. A CON
                 * is in transit as long as it has not been acknowledged, rejected,
                 * or timed out. All further notifications are postponed here. If a
                 * former CON is acknowledged or timeouts, it starts the freshest
                 * notification (In case of a timeout, it keeps the retransmission
                 * counter). When a fresh/younger notification arrives but must be
                 * postponed we forget any former notification.
                 */
                if (response.Type == MessageType.CON) {
                    PrepareSelfReplacement(nextLayer, exchange, response);
                }

                // The decision whether to postpone this notification or not and the
                // decision which notification is the freshest to send next must be
                // synchronized
                lock (exchange) {
                    Response current = relation.CurrentControlNotification;
                    if (current != null && IsInTransit(current)) {
                        if (log.IsDebugEnabled) {
                            log.Debug("A former notification is still in transit. Postpone " + response);
                        }
                        // use the same ID
                        response.ID = current.ID;
                        relation.NextControlNotification = response;
                        return;
                    }
                    else { 
                        relation.CurrentControlNotification = response;
                        relation.NextControlNotification = null;
                    }
                }
            }

            // else no observe was requested or the resource does not allow it
            base.SendResponse(nextLayer, exchange, response);
        }

        /// <inheritdoc/>
        public override void ReceiveResponse(INextLayer nextLayer, Exchange exchange, Response response)
        {
            if (response.HasOption(OptionType.Observe)) {
                if (exchange.Request.IsCancelled) {
                    // The request was canceled and we no longer want notifications
                    if (log.IsDebugEnabled) {
                        log.Debug("ObserveLayer rejecting notification for canceled Exchange");
                    }

                    EmptyMessage rst = EmptyMessage.NewRST(response);
                    SendEmptyMessage(nextLayer, exchange, rst);
                    // Matcher sets exchange as complete when RST is sent
                }
                else {
                    PrepareReregistration(exchange, response, msg => SendRequest(nextLayer, exchange, msg));
                    base.ReceiveResponse(nextLayer, exchange, response);
                }
            }
            else {
                // No observe option in response => always deliver
                base.ReceiveResponse(nextLayer, exchange, response);
            }
        }

        /// <inheritdoc/>
        public override void ReceiveEmptyMessage(INextLayer nextLayer, Exchange exchange, EmptyMessage message)
        {
            // NOTE: We could also move this into the MessageObserverAdapter from
            // sendResponse into the method rejected().
            if (message.Type == MessageType.RST && exchange.Origin == Origin.Remote)
            {
                // The response has been rejected
                ObserveRelation relation = exchange.Relation;
                if (relation != null)
                {
                    relation.Cancel();
                } // else there was no observe relation ship and this layer ignores the rst
            }
            base.ReceiveEmptyMessage(nextLayer, exchange, message);
        }

        private static Boolean IsInTransit(Response response)
        {
            MessageType type = response.Type;
            Boolean acked = response.IsAcknowledged;
            Boolean timeout = response.IsTimedOut;
            Boolean result = type == MessageType.CON && !acked && !timeout;
            return result;
        }

        private void PrepareSelfReplacement(INextLayer nextLayer, Exchange exchange, Response response)
        {
            response.Acknowledged += (o, e) => {
                lock (exchange) {
                    ObserveRelation relation = exchange.Relation;
                    Response next = relation.NextControlNotification;
                    relation.CurrentControlNotification = next; // next may be null
                    relation.NextControlNotification = null;
                    if (next != null) {
                        if (log.IsDebugEnabled) {
                            log.Debug("Notification has been acknowledged, send the next one");
                        }
                        // this is not a self replacement, hence a new ID
                        next.ID = Message.None;
                        // Create a new task for sending next response so that we can leave the sync-block
                        Executor.Start(() => SendResponse(nextLayer, exchange, next));
                    }
                }
            };

            response.Retransmitting += (o, e) => {
                lock (exchange) {
                    ObserveRelation relation = exchange.Relation;
                    Response next = relation.NextControlNotification;
                    if (next != null) {
                        if (log.IsDebugEnabled) {
                            log.Debug("The notification has timed out and there is a fresher notification for the retransmission.");
                        }
                        // Cancel the original retransmission and send the fresh notification here
                        response.IsCancelled = true;
                        // use the same ID
                        next.ID = response.ID;
                        // Convert all notification retransmissions to CON
                        if (next.Type != MessageType.CON) {
                            next.Type = MessageType.CON;
                            PrepareSelfReplacement(nextLayer, exchange, next);
                        }
                        relation.CurrentControlNotification = next;
                        relation.NextControlNotification = null;
                        // Create a new task for sending next response so that we can leave the sync-block
                        Executor.Start(() => SendResponse(nextLayer, exchange, next));
                    }
                }
            };

            response.TimedOut += (o, e) => {
                ObserveRelation relation = exchange.Relation;
                if (log.IsDebugEnabled) {
                    log.Debug($"Notification {relation.Exchange.Request.TokenString} timed out. Cancel all relations with source {relation.Source}");
                }
                relation.CancelAll();
            };
        }

        private void PrepareReregistration(Exchange exchange, Response response, Action<Request> reregister)
        {
            if (!exchange.Request.ObserveReconnect) return;

            Int64 timeout = response.MaxAge * 1000 + _backoff + _Random.Next(2, 15) * 1000;
            ReregistrationContext ctx = exchange.GetOrAdd<ReregistrationContext>(
                ReregistrationContextKey, _ => new ReregistrationContext(exchange, timeout, reregister));

            if (log.IsDebugEnabled) {
                log.Debug("Scheduling re-registration in " + timeout + "ms for " + exchange.Request);
            }

            ctx.Restart();
        }

        class ReregistrationContext : IDisposable
        {
            private readonly Exchange _exchange;
            private readonly Action<Request> _reregister;
            private Timer _timer;
#if NETSTANDARD1_3
            private int _timeout;
#endif

            public ReregistrationContext(Exchange exchange, Int64 timeout, Action<Request> reregister)
            {
                _exchange = exchange;
                _reregister = reregister;
#if NETSTANDARD1_3
                _timeout = (int) timeout;
#else
                _timer = new Timer(timeout) {
                    AutoReset = false
                };
                _timer.Elapsed += timer_Elapsed;
#endif
            }

            public void Start()
            {
#if NETSTANDARD1_3
                _timer = new Timer(timer_Elapsed, this, _timeout, Timeout.Infinite);
#else
                _timer.Start();
#endif
            }

            public void Restart()
            {
                Stop();
                Start();
            }

            public void Stop()
            {
#if NETSTANDARD1_3
                if (_timer != null) {
                    _timer.Dispose();
                }
                _timer = null;
#else
                _timer.Stop();
#endif
            }

            public void Cancel()
            {
                Stop();
                Dispose();
            }

            public void Dispose()
            {
                _timer.Dispose();
            }

#if NETSTANDARD1_3
            static void timer_Elapsed(Object obj)
            {
                ReregistrationContext sender = obj as ReregistrationContext;
                Request request = sender._exchange.Request;
                if (!request.IsCancelled) {
                    Request refresh = Request.NewGet();
                    refresh.SetOptions(request.GetOptions());
                    // make sure Observe is set and zero
                    refresh.MarkObserve();
                    // use same Token
                    refresh.Token = request.Token;
                    refresh.Destination = request.Destination;
                    refresh.CopyEventHandler(request);
                    if (log.IsDebugEnabled) {
                        log.Debug("Re-registering for " + request);
                    }
                    request.FireReregister(refresh);
                    sender._reregister(refresh);
                }
                else {
                    if (log.IsDebugEnabled) {
                        log.Debug("Dropping re-registration for canceled " + request);
                    }
                }
            }
#else
            void timer_Elapsed(Object sender, ElapsedEventArgs e)
            {
                Request request = _exchange.Request;
                if (!request.IsCancelled) {
                    Request refresh = new Request(request.Method);
                    refresh.SetOptions(request.GetOptions());
                    // make sure Observe is set and zero
                    refresh.MarkObserve();
                    // use same Token
                    refresh.Token = request.Token;
                    refresh.Destination = request.Destination;
                    refresh.CopyEventHandler(request);
                    if (log.IsDebugEnabled) {
                        log.Debug("Re-registering for " + request);
                    }
                    request.FireReregister(refresh);
                    _reregister(refresh);
                }
                else {
                    if (log.IsDebugEnabled) {
                        log.Debug("Dropping re-registration for canceled " + request);
                    }
                }
            }
#endif
        }
    }
}
