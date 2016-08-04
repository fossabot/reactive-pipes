using System;
using System.Collections.Generic;
using System.Linq;
using NCrontab;
using Newtonsoft.Json;

namespace reactive.pipes.Scheduler
{
    public class ScheduledTask
    {
        public int Id { get; set; }
        public int Priority { get; set; }
        public int Attempts { get; set; }
        public string Handler { get; set; }
        public DateTimeOffset RunAt { get; set; }
        public TimeSpan? MaximumRuntime { get; set; }
        public int? MaximumAttempts { get; set; }
        public bool? DeleteOnSuccess { get; set; }
        public bool? DeleteOnFailure { get; set; }
        public bool? DeleteOnError { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string LastError { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
        public DateTimeOffset? SucceededAt { get; set; }
        public DateTimeOffset? LockedAt { get; set; }
        public string LockedBy { get; set; }

        public ScheduledTask()
        {
            Tags = new HashSet<string>();
        }

        #region Tagging

        public HashSet<string> Tags { get; set; }

        #endregion

        #region Scheduling

        public string Expression { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset? End { get; set; }
        public bool ContinueOnSuccess { get; set; } = true;
        public bool ContinueOnFailure { get; set; } = true;
        public bool ContinueOnError { get; set; } = true;

        public bool RunningOvertime
        {
            get
            {
                if (!LockedAt.HasValue)
                    return false;
                if (!MaximumRuntime.HasValue)
                    return false;

                DateTimeOffset now = DateTimeOffset.UtcNow;
                TimeSpan elapsed = LockedAt.Value - now;

                // overtime = 125% of maximum runtime
                long overage = (long)(MaximumRuntime.Value.Ticks / 0.25f);
                TimeSpan overtime = MaximumRuntime.Value + new TimeSpan(overage);

                if (elapsed >= overtime)
                    return true;

                return false;
            }
        }

        [JsonIgnore] public DateTimeOffset? NextOccurrence => GetNextOccurence();
        [JsonIgnore] public DateTimeOffset? LastOccurrence => GetLastOccurrence();
        [JsonIgnore] public IEnumerable<DateTimeOffset> AllOccurrences => GetAllOccurrences();
        
        private IEnumerable<DateTimeOffset> GetAllOccurrences()
        {
            if (string.IsNullOrWhiteSpace(Expression))
                return Enumerable.Empty<DateTimeOffset>();

            if (!End.HasValue)
                throw new ArgumentException("You cannot request all occurrences of an infinite series", nameof(End));

            return GetFiniteSeriesOccurrences(End.Value);
        }

        private DateTimeOffset? GetNextOccurence()
        {
            if (string.IsNullOrWhiteSpace(Expression))
                return null;

            // important: never iterate occurrences, the series could be inadvertently huge (i.e. 100 years of seconds)
            return End == null ? GetNextOccurrenceInInfiniteSeries() : GetFiniteSeriesOccurrences(End.Value).FirstOrDefault();
        }

        private DateTimeOffset? GetLastOccurrence()
        {
            if (string.IsNullOrWhiteSpace(Expression))
                return null;

            if (!End.HasValue)
                throw new ArgumentException("You cannot request the last occurrence of an infinite series", nameof(End));

            return GetFiniteSeriesOccurrences(End.Value).Last();
        }

        private DateTimeOffset? GetNextOccurrenceInInfiniteSeries()
        {
            CrontabSchedule schedule = TryParseCron();
            if (schedule == null)
                return null;

            DateTime nextOccurrence = schedule.GetNextOccurrence(RunAt.DateTime.ToLocalTime());

            return new DateTimeOffset(nextOccurrence.ToUniversalTime());
        }

        private IEnumerable<DateTimeOffset> GetFiniteSeriesOccurrences(DateTimeOffset end)
        {
            CrontabSchedule schedule = TryParseCron();
            if (schedule == null)
                return Enumerable.Empty<DateTimeOffset>();

            IEnumerable<DateTime> nextOccurrences = schedule.GetNextOccurrences(RunAt.DateTime, end.DateTime);
            IEnumerable<DateTimeOffset> occurrences = nextOccurrences.Select(o => new DateTimeOffset(o.ToUniversalTime()));
            return occurrences;
        }

        private CrontabSchedule TryParseCron()
        {
            if (string.IsNullOrWhiteSpace(Expression))
                return null;

            return CrontabSchedule.TryParse(Expression);
        }

        #endregion
    }
}