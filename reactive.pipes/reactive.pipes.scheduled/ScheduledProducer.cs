using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Schedulers;
using ImpromptuInterface;
using Newtonsoft.Json;
using reactive.pipes.Producers;

namespace reactive.pipes.scheduled
{
    public class ScheduledProducer : BackgroundProducer<IEnumerable<ScheduledTask>>
    {
        private static readonly IDictionary<HandlerInfo, Handler> HandlerCache = new ConcurrentDictionary<HandlerInfo, Handler>();
        private static readonly IDictionary<Type, HandlerMethods> MethodCache = new ConcurrentDictionary<Type, HandlerMethods>();

        private readonly ConcurrentDictionary<int, TaskScheduler> _schedulers;
        private readonly ConcurrentDictionary<TaskScheduler, TaskFactory> _factories;
        private readonly ConcurrentDictionary<Handler, HandlerMethods> _pending;

        private QueuedTaskScheduler _scheduler;
        private CancellationTokenSource _cancel;
        private readonly int _threads;
        private readonly ScheduledProducerSettings _settings;

        public ScheduledProducerSettings Settings
        {
            get
            {
                var @readonly = new ScheduledProducerSettings
                {
                    DelayTasks = _settings.DelayTasks,
                    TypeResolver = _settings.TypeResolver,
                    Store = _settings.Store,
                    Concurrency = _settings.Concurrency,
                    SleepInterval = _settings.SleepInterval,
                    IntervalFunction = _settings.IntervalFunction,
                    ReadAhead = _settings.ReadAhead,

                    MaximumAttempts = _settings.MaximumAttempts,
                    MaximumRuntime = _settings.MaximumRuntime,
                    DeleteOnError = _settings.DeleteOnError,
                    DeleteOnFailure = _settings.DeleteOnFailure,
                    DeleteOnSuccess = _settings.DeleteOnSuccess,
                    Priority = _settings.Priority
                };

                return @readonly;
            }
        }
       
        public ScheduledProducer(ScheduledProducerSettings settings = null)
        {
            _settings = settings ?? new ScheduledProducerSettings();
            
            _schedulers = new ConcurrentDictionary<int, TaskScheduler>();
            _factories = new ConcurrentDictionary<TaskScheduler, TaskFactory>();
            _pending = new ConcurrentDictionary<Handler, HandlerMethods>();
            _cancel = new CancellationTokenSource();
            _threads = _settings.Concurrency;

            Background.Attach(WithPendingTasks);
            Background.AttachBacklog(WithOverflowTasks);
            Background.AttachUndeliverable(WithFailedTasks);
        }

        private IEnumerable<ScheduledTask> SeedTasksFromQueue()
        {
            return _settings.Store.GetAndLockNextAvailable(_settings.ReadAhead);
        }

        public override void Start(bool immediate = false)
        {
            if (_scheduler == null)
                _scheduler = new QueuedTaskScheduler(_threads);
            
            Background.Produce(SeedTasksFromQueue, _settings.SleepInterval);

            base.Start(immediate);
        }

        public override void Stop(bool immediate = false)
        {
            Parallel.ForEach(_pending.Where(entry => entry.Value.OnHalt != null), e =>
            {
                e.Value.OnHalt.Halt(immediate);
            });

            _pending.Clear();

            if (_scheduler != null)
            {
                _scheduler.Dispose();
                _scheduler = null;
            }

            base.Stop(immediate);
        }
        
        private void WithFailedTasks(IEnumerable<ScheduledTask> scheduledTasks)
        {
            // This should be impossible; we only use the pipeline to seed from a backing store, which is all or nothing
            WithPendingTasks(scheduledTasks);    
        }

        private void WithOverflowTasks(IEnumerable<ScheduledTask> scheduledTasks)
        {
            // We could have been shutting down, which is not materially different than if we had succeeded, so we should process these
            WithPendingTasks(scheduledTasks);
        }

        private void WithPendingTasks(IEnumerable<ScheduledTask> scheduledTasks)
        {
            // assign tasks to scheduler slots
            var runtimes = new Dictionary<Task, TimeSpan>();
            var pendingTasks = new Dictionary<Task, CancellationTokenSource>();

            foreach (ScheduledTask scheduledTask in scheduledTasks)
            {
                TaskScheduler scheduler = AcquireScheduler(scheduledTask);
                CancellationTokenSource cancel = new CancellationTokenSource();
                TaskFactory taskFactory = _factories[scheduler];
                Task task = taskFactory.StartNew(() =>
                {
                    AttemptTask(scheduledTask);
                }, cancel.Token);
                pendingTasks.Add(task, cancel);
                runtimes.Add(task, scheduledTask.MaximumRuntime.GetValueOrDefault());
            }

            // wait for execution of cancellable tasks
            Parallel.ForEach(pendingTasks, performer =>
            {
                if (!Task.WaitAll(new[] { performer.Key }, runtimes[performer.Key]))
                {
                    performer.Value.Cancel();
                }
            });
        }

        internal bool AttemptTask(ScheduledTask task, bool persist = true)
        {
            if (_cancel.IsCancellationRequested)
                return false;

            Exception exception;

            var success = AttemptCycle(task, out exception);

            if (persist)
                SaveTask(task, success, exception);

            _cancel.Token.ThrowIfCancellationRequested();

            return success;
        }

        private bool AttemptCycle(ScheduledTask job, out Exception exception)
        {
            job.Attempts++;
            var success = Perform(job, out exception);
            if (!success)
            {
                var dueTime = DateTimeOffset.UtcNow + _settings.IntervalFunction(job.Attempts);
                job.RunAt = dueTime;
            }
            return success;
        }

        private void SaveTask(ScheduledTask task, bool success, Exception exception)
        {
            bool deleted = false;

            var now = DateTimeOffset.UtcNow;

            if (!success)
            {
                if (JobWillFail(task))
                {
                    if (task.DeleteOnFailure.HasValue && task.DeleteOnFailure.Value)
                    {
                        _settings.Store.Delete(task);
                        deleted = true;
                    }
                    task.FailedAt = now;
                }
            }
            else
            {
                if (task.DeleteOnSuccess.HasValue && task.DeleteOnSuccess.Value)
                {
                    _settings.Store.Delete(task);
                    deleted = true;
                }
                task.SucceededAt = now;
            }

            if (!deleted)
            {
                // unlock for other workers
                task.LockedAt = null;
                task.LockedBy = null;

                _settings.Store.Save(task);

                if (task.NextOccurrence != null)
                {
                    var shouldRepeat = (success && task.ContinueOnSuccess) ||
                                       (!success && task.ContinueOnFailure) ||
                                       (exception != null && task.ContinueOnError);

                    if (shouldRepeat)
                    {
                        task.Start = task.RunAt;
                        DateTimeOffset? nextOccurrence = task.NextOccurrence;

                        var clone = new ScheduledTask
                        {
                            Priority = task.Priority,
                            Handler = task.Handler,
                            DeleteOnSuccess = task.DeleteOnSuccess,
                            DeleteOnFailure = task.DeleteOnFailure,
                            DeleteOnError = task.DeleteOnError,
                            Expression = task.Expression,
                            Start = task.Start,
                            End = task.End,
                            ContinueOnSuccess = task.ContinueOnSuccess,
                            ContinueOnFailure = task.ContinueOnFailure,
                            ContinueOnError = task.ContinueOnError,
                            RunAt = nextOccurrence.GetValueOrDefault(),
                            MaximumAttempts = task.MaximumAttempts,
                            MaximumRuntime = task.MaximumRuntime
                        };

                        _settings.Store.Save(clone);
                    }
                }
            }
        }

        private static bool JobWillFail(ScheduledTask task)
        {
            return task.Attempts >= task.MaximumAttempts;
        }

        private bool Perform(ScheduledTask task, out Exception exception)
        {
            var success = false;

            // Acquire the handler:
            HandlerInfo handlerInfo = JsonConvert.DeserializeObject<HandlerInfo>(task.Handler);
            Handler handler;
            if (!HandlerCache.TryGetValue(handlerInfo, out handler))
            {
                string typeName = $"{handlerInfo.Namespace}.{handlerInfo.Entrypoint}";
                Type type = _settings.TypeResolver.FindTypeByName(typeName);
                if (type != null)
                {
                    var instance = Activator.CreateInstance(type);
                    if (instance != null)
                    {
                        handler = TryWrapHook<Handler>(instance);
                        if (handler != null)
                            HandlerCache.Add(handlerInfo, handler);
                    }
                }
            }

            if (handler == null)
            {
                task.LastError = "Missing or invalid handler";
                exception = null;
                return false;
            }

            // Acquire and cache method manifest:
            var methods = CacheOrCreateMethods(handler);

            _pending.TryAdd(handler, methods);
            try
            {
                // Before:
                bool? before = methods.OnBefore?.Before();

                // Handler:
                if (!before.HasValue || before.Value)
                    success = handler.Perform();

                // Success:
                if (success)
                    methods.OnSuccess?.Success();

                // Failure:
                if (JobWillFail(task))
                    methods.OnFailure?.Failure();

                // After:
                methods.OnAfter?.After();

                exception = null;
            }
            catch (OperationCanceledException oce)
            {
                task.LastError = "Cancelled";
                exception = oce;
            }
            catch (Exception ex)
            {
                task.LastError = ex.Message;
                methods?.OnError?.Error(ex);
                exception = ex;
            }
            finally
            {
                _pending.TryRemove(handler, out methods);
            }

            return success;
        }

        private static HandlerMethods CacheOrCreateMethods(Handler handler)
        {
            Type handlerType = handler.GetType();
            HandlerMethods methods;
            if (!MethodCache.TryGetValue(handlerType, out methods))
            {
                MethodCache.Add(handlerType, methods = new HandlerMethods
                {
                    Handler = handler,

                    OnBefore = TryWrapHook<Before>(handler),
                    OnAfter = TryWrapHook<After>(handler),
                    OnSuccess = TryWrapHook<Success>(handler),
                    OnFailure = TryWrapHook<Failure>(handler),
                    OnError = TryWrapHook<Error>(handler),
                    OnHalt = TryWrapHook<Halt>(handler)
                });
            }
            return methods;
        }

        private static T TryWrapHook<T>(object instance) where T : class
        {
            var prototype = typeof(T).GetMethods();
            var example = instance.GetType().GetMethods();
            var match = prototype.Any(l => example.Any(r => AreMethodsDuckEquivalent(l, r))) ? instance.ActLike<T>() : null;
            return match;
        }

        private static bool AreMethodsDuckEquivalent(MethodInfo left, MethodInfo right)
        {
            if (left == null || right == null)
                return false;
            if (!left.Name.Equals(right.Name))
                return false;
            if (left.Equals(right) || left.GetHashCode() == right.GetHashCode())
                return true;

            ParameterInfo[] lp = left.GetParameters();
            ParameterInfo[] rp = right.GetParameters();
            if (lp.Length != rp.Length)
                return false;
            if (lp.Where((t, i) => t.ParameterType != rp[i].ParameterType).Any())
                return false;

            return left.ReturnType == right.ReturnType;
        }

        private TaskScheduler AcquireScheduler(ScheduledTask task)
        {
            TaskScheduler scheduler;
            if (!_schedulers.TryGetValue(task.Priority, out scheduler))
            {
                scheduler = _scheduler.ActivateNewQueue(task.Priority);
                TaskFactory factory = new TaskFactory(_cancel.Token, TaskCreationOptions.LongRunning, TaskContinuationOptions.LongRunning, scheduler);
                _schedulers.TryAdd(task.Priority, scheduler);
                _factories.TryAdd(scheduler, factory);
            }
            return scheduler;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }
            if (_cancel != null)
            {
                _cancel.Cancel();
                _cancel.Token.WaitHandle.WaitOne();
                _cancel.Dispose();
                _cancel = null;
            }
            _factories.Clear();
            _schedulers.Clear();
            if (_scheduler == null)
            {
                return;
            }
            _scheduler.Dispose();
            _scheduler = null;
        }
    }
}