using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Caliburn.Micro;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Helpers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Setting;
using Ciribob.DCS.SimpleRadio.Standalone.Server.API.Routes;
using Ciribob.DCS.SimpleRadio.Standalone.Server.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Server.Settings;
using Ciribob.DCS.SimpleRadio.Standalone.Server.UI.ClientAdmin;
using Ciribob.DCS.SimpleRadio.Standalone.Server.UI.MainWindow;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using LogManager = NLog.LogManager;

namespace Ciribob.DCS.SimpleRadio.Standalone.Server
{
    public class Bootstrapper : BootstrapperBase
    {
        private readonly SimpleContainer _simpleContainer = new SimpleContainer();
        private bool loggingReady = false;
        private HttpListener _httpListener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public Bootstrapper()
        {
            Initialize();
            SetupLogging();

            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            Analytics.Log("Server", "Startup", Guid.NewGuid().ToString());

            InitializeHttpServer();
        }

        private void InitializeHttpServer()
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://127.0.0.1:8080/");
            _cancellationTokenSource = new CancellationTokenSource();

            Task.Run(() => StartHttpServer(_cancellationTokenSource.Token));
        }

        private async Task StartHttpServer(CancellationToken cancellationToken)
        {
            _httpListener.Start();
            _logger.Info("HTTP Server started. Listening on http://127.0.0.1:8080/");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await _httpListener.GetContextAsync();
                    ProcessRequest(context);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error processing HTTP request");
                }
            }

            _httpListener.Stop();
            _logger.Info("HTTP Server stopped.");
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            string path = context.Request.Url.AbsolutePath;
            string method = context.Request.HttpMethod;

            string responseString = "Invalid request";
            int statusCode = 400;

            if (method == "POST")
            {
                if (path.StartsWith("/kick/"))
                {
                    Response response = Kick.Handle(context, _logger);
                    responseString = response.Message;
                    statusCode = response.StatusCode;
                }
                else if (path.StartsWith("/ban/"))
                {
                    Response response = Ban.Handle(context, _logger);
                    responseString = response.Message;
                    statusCode = response.StatusCode;
                }
                else
                {
                    responseString = "Endpoint not found";
                    statusCode = 404;
                }
            }
            else
            {
                responseString = "Method not allowed";
                statusCode = 405;
            }

            SendResponse(context, responseString, statusCode);
        }

        private void SendResponse(HttpListenerContext context, string responseString, int statusCode)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);

            context.Response.StatusCode = statusCode;
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }

        private void SetupLogging()
        {
            // If there is a configuration file then this will already be set
            if (LogManager.Configuration != null)
            {
                loggingReady = true;
                return;
            }

            var config = new LoggingConfiguration();
            var fileTarget = new FileTarget
            {
                FileName = "serverlog.txt",
                ArchiveFileName = "serverlog.old.txt",
                MaxArchiveFiles = 1,
                ArchiveAboveSize = 104857600,
                Layout =
                @"${longdate} | ${logger} | ${message} ${exception:format=toString,Data:maxInnerExceptionLevel=1}"
            };

            var wrapper = new AsyncTargetWrapper(fileTarget, 5000, AsyncTargetWrapperOverflowAction.Discard);
            config.AddTarget("asyncFileTarget", wrapper);
            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Info, wrapper));

            // only add transmission logging at launch if its enabled, defer rule and target creation otherwise
            if (ServerSettingsStore.Instance.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED).BoolValue)
            {
                config = LoggingHelper.GenerateTransmissionLoggingConfig(config,
                            ServerSettingsStore.Instance.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION).IntValue);
            }

            LogManager.Configuration = config;
            loggingReady = true;
        }

        protected override void Configure()
        {
            _simpleContainer.Singleton<IWindowManager, WindowManager>();
            _simpleContainer.Singleton<IEventAggregator, EventAggregator>();
            _simpleContainer.Singleton<ServerState>();

            _simpleContainer.Singleton<MainViewModel>();
            _simpleContainer.Singleton<ClientAdminViewModel>();
        }

        protected override object GetInstance(Type service, string key)
        {
            var instance = _simpleContainer.GetInstance(service, key);
            if (instance != null)
                return instance;

            throw new InvalidOperationException("Could not locate any instances.");
        }

        protected override IEnumerable<object> GetAllInstances(Type service)
        {
            return _simpleContainer.GetAllInstances(service);
        }


        protected override void OnStartup(object sender, StartupEventArgs e)
        {
            IDictionary<string, object> settings = new Dictionary<string, object>
            {
                {"Icon", new BitmapImage(new Uri("pack://application:,,,/SR-Server;component/server-10.ico"))},
                {"ResizeMode", ResizeMode.CanMinimize}
            };
            //create an instance of serverState to actually start the server
            _simpleContainer.GetInstance(typeof(ServerState), null);

            DisplayRootViewFor<MainViewModel>(settings);

            UpdaterChecker.CheckForUpdate(Settings.ServerSettingsStore.Instance.GetServerSetting(Common.Setting.ServerSettingsKeys.CHECK_FOR_BETA_UPDATES).BoolValue);
        }

        protected override void BuildUp(object instance)
        {
            _simpleContainer.BuildUp(instance);
        }


        protected override void OnExit(object sender, EventArgs e)
        {
            var serverState = (ServerState)_simpleContainer.GetInstance(typeof(ServerState), null);
            serverState.StopServer();

            _cancellationTokenSource.Cancel();
            base.OnExit(sender, e);
        }

        protected override void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (loggingReady)
            {
                Logger logger = LogManager.GetCurrentClassLogger();
                logger.Error(e.Exception, "Received unhandled exception, exiting");
            }

            base.OnUnhandledException(sender, e);
        }
    }
}