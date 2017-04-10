using System;
using System.Diagnostics;

namespace Paket.Bootstrapper.DownloadStrategies
{
    public abstract class DownloadStrategy : IDownloadStrategy
    {
        public abstract string Name { get; }
        public IDownloadStrategy FallbackStrategy { get; set; }
        public string GetLatestVersion(bool ignorePrerelease)
        {
            return Wrap(() => GetLatestVersionCore(ignorePrerelease), "GetLatestVersion");
        }

        public void DownloadVersion(string latestVersion, string target)
        {
            Wrap(() => DownloadVersionCore(latestVersion, target), "DownloadVersion");
        }

        public void SelfUpdate(string latestVersion)
        {
            Wrap(() => SelfUpdateCore(latestVersion), "SelfUpdate");
        }

        protected abstract string GetLatestVersionCore(bool ignorePrerelease);
        protected abstract void DownloadVersionCore(string latestVersion, string target);
        protected abstract void SelfUpdateCore(string latestVersion);

        private void Wrap(Action action, string actionName)
        {
            if (!ConsoleImpl.IsTraceEnabled)
            {
                action();
                return;
            }

            Wrap(() => {
                action();
                return "void";
            }, actionName);
        }

        private TResult Wrap<TResult>(Func<TResult> func, string actionName)
        {
            if (!ConsoleImpl.IsTraceEnabled)
            {
                return func();
            }

            ConsoleImpl.WriteTrace("[{0}] {1}...", Name, actionName);
            var watch = Stopwatch.StartNew();
            try
            {
                var result = func();
                watch.Stop();
                ConsoleImpl.WriteTrace("[{0}] {1} took {2:0.##} second(s) and returned {3}.", Name, actionName, watch.Elapsed.TotalSeconds, result);
                return result;
            }
            catch (Exception exception)
            {
                watch.Stop();
                ConsoleImpl.WriteTrace("[{0}] {1} took {2:0.##} second(s) and failed with {3}.", Name, actionName, watch.Elapsed.TotalSeconds, exception.Message);
                throw;
            }
        }
    }
}