﻿using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Eavesdrop.Network;

using BrotliSharpLib;

namespace Eavesdrop
{
    public static class Eavesdropper
    {
        private static TcpListener _listener;
        private static readonly object _stateLock;
        private static TaskCompletionSource<bool> _terminationSource;

        public delegate Task AsyncEventHandler<TEventArgs>(object sender, TEventArgs e);

        public static event AsyncEventHandler<RequestInterceptedEventArgs> RequestInterceptedAsync;
        private static async Task OnRequestInterceptedAsync(RequestInterceptedEventArgs e)
        {
            Task interceptedTask = RequestInterceptedAsync?.Invoke(null, e);
            if (interceptedTask != null)
            {
                await interceptedTask;
            }
        }

        public static event AsyncEventHandler<ResponseInterceptedEventArgs> ResponseInterceptedAsync;
        private static async Task OnResponseInterceptedAsync(ResponseInterceptedEventArgs e)
        {
            Task interceptedTask = ResponseInterceptedAsync?.Invoke(null, e);
            if (interceptedTask != null)
            {
                await interceptedTask;
            }
        }

        public static List<string> Overrides { get; }
        public static bool IsRunning { get; private set; }
        public static CertificateManager Certifier { get; }

        static Eavesdropper()
        {
            _stateLock = new object();

            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

            Overrides = new List<string>();
            Certifier = new CertificateManager("Eavesdrop", "Eavesdrop Root Certificate Authority");
        }

        public static void Terminate()
        {
            lock (_stateLock)
            {
                ResetMachineProxy();
                IsRunning = false;

                if (_listener != null)
                {
                    _terminationSource = new TaskCompletionSource<bool>();

                    _listener.Stop();
                    _listener = null;

                    _terminationSource.Task.Wait();
                }
            }
        }
        public static void Initiate(int port)
        {
            Initiate(port, Interceptors.Default);
        }
        public static void Initiate(int port, Interceptors interceptors)
        {
            lock (_stateLock)
            {
                Terminate();

                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();

                IsRunning = true;

                Task.Factory.StartNew(InterceptRequestAsnync, TaskCreationOptions.LongRunning);
                SetMachineProxy(port, interceptors);
            }
        }

        private static async Task InterceptRequestAsnync()
        {
            while (IsRunning && _listener != null)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Task handleClientAsync = HandleClientAsync(client);
                }
                catch (ObjectDisposedException) { }
            }
            _terminationSource?.SetResult(true);
        }
        private static async Task HandleClientAsync(TcpClient client)
        {
            await Task.Yield();
            using (var local = new EavesNode(Certifier, client))
            {
                WebRequest request = await local.ReadRequestAsync().ConfigureAwait(false);
                if (request == null) return;

                HttpContent requestContent = null;
                var requestArgs = new RequestInterceptedEventArgs(request);
                try
                {
                    if (request.ContentLength > 0)
                    {
                        byte[] payload = await EavesNode.GetPayloadAsync(local.GetStream(), request.ContentLength).ConfigureAwait(false);
                        if (payload?.Length > 0)
                        {
                            requestContent = new ByteArrayContent(payload);
                            requestArgs.Content = requestContent;
                        }
                    }

                    await OnRequestInterceptedAsync(requestArgs).ConfigureAwait(false);
                    request = requestArgs.Request;

                    if (requestArgs.Cancel) return;
                    if (requestArgs.Content != null)
                    {
                        if (request.Headers[HttpRequestHeader.ContentEncoding] == "br")
                        {
                            byte[] payload = await requestArgs.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            byte[] compressedPayload = Brotli.CompressBuffer(payload, 0, payload.Length);

                            requestArgs.Content.Dispose();
                            requestArgs.Content = new ByteArrayContent(compressedPayload);
                        }
                        request.ContentLength = (long)requestArgs.Content.Headers.ContentLength;

                        using (requestArgs.Content)
                        using (Stream requestOutput = await request.GetRequestStreamAsync().ConfigureAwait(false))
                        {
                            await requestArgs.Content.CopyToAsync(requestOutput).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    requestContent?.Dispose();
                    requestArgs.Content?.Dispose();
                }


                WebResponse response = null;
                try { response = await request.GetResponseAsync().ConfigureAwait(false); }
                catch (WebException ex) { response = ex.Response; }
                catch (ProtocolViolationException)
                {
                    response?.Dispose();
                    response = null;
                }

                if (response == null) return;
                HttpContent responseContent = null;
                var responseArgs = new ResponseInterceptedEventArgs(request, response);
                try
                {
                    byte[] payload = await EavesNode.GetPayloadAsync(response).ConfigureAwait(false);
                    if (payload?.Length > 0)
                    {
                        responseContent = new ByteArrayContent(payload);
                        responseArgs.Content = responseContent;
                    }
                    await OnResponseInterceptedAsync(responseArgs).ConfigureAwait(false);
                    if (responseArgs.Cancel) return;

                    if (responseArgs.Content != null && response.Headers[HttpResponseHeader.ContentEncoding] == "br")
                    {
                        byte[] newPayload = await responseArgs.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        byte[] compressedPayload = Brotli.CompressBuffer(newPayload, 0, newPayload.Length);

                        responseArgs.Content.Dispose();
                        responseArgs.Content = new ByteArrayContent(compressedPayload);
                    }

                    await local.SendResponseAsync(responseArgs.Response, responseArgs.Content).ConfigureAwait(false);
                }
                finally
                {
                    response.Dispose();
                    responseArgs.Response.Dispose();

                    responseContent?.Dispose();
                    responseArgs.Content?.Dispose();
                }
            }
        }

        private static void ResetMachineProxy()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            INETOptions.Overrides.Clear();
            INETOptions.IsIgnoringLocalTraffic = false;

            INETOptions.HTTPAddress = null;
            INETOptions.HTTPSAddress = null;
            INETOptions.IsProxyEnabled = false;

            INETOptions.Save();
        }
        private static void SetMachineProxy(int port, Interceptors interceptors)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            foreach (string @override in Overrides)
            {
                if (INETOptions.Overrides.Contains(@override)) continue;
                INETOptions.Overrides.Add(@override);
            }

            string address = ("127.0.0.1:" + port);
            if (interceptors.HasFlag(Interceptors.HTTP))
            {
                INETOptions.HTTPAddress = address;
            }
            if (interceptors.HasFlag(Interceptors.HTTPS))
            {
                INETOptions.HTTPSAddress = address;
            }
            INETOptions.IsProxyEnabled = true;
            INETOptions.IsIgnoringLocalTraffic = true;

            INETOptions.Save();
        }
    }
}