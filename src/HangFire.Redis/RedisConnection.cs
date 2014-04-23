// This file is part of HangFire.
// Copyright � 2013-2014 Sergey Odinokov.
// 
// HangFire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// HangFire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with HangFire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using HangFire.Common;
using HangFire.Server;
using HangFire.States;
using HangFire.Storage;
using ServiceStack.Redis;

namespace HangFire.Redis
{
    internal class RedisConnection : IStorageConnection
    {
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(1);
        private readonly IRedisClient _redis;

        public RedisConnection(JobStorage storage, IRedisClient redis)
        {
            _redis = redis;
            Storage = storage;
        }

        public JobStorage Storage { get; private set; }

        public void Dispose()
        {
            _redis.Dispose();
        }

        public IStateMachine CreateStateMachine()
        {
            return new StateMachine(this);
        }

        public IWriteOnlyTransaction CreateWriteTransaction()
        {
            return new RedisWriteOnlyTransaction(_redis.CreateTransaction());
        }

        public ProcessingJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
        {
            string jobId;
            string queueName;
            var queueIndex = 0;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                queueIndex = (queueIndex + 1) % queues.Length;
                queueName = queues[queueIndex];

                var queueKey = RedisStorage.Prefix + String.Format("queue:{0}", queueName);
                var fetchedKey = RedisStorage.Prefix + String.Format("queue:{0}:dequeued", queueName);

                if (queueIndex == 0)
                {
                    jobId = _redis.BlockingPopAndPushItemBetweenLists(
                        queueKey,
                        fetchedKey,
                        FetchTimeout);
                }
                else
                {
                    jobId = _redis.PopAndPushItemBetweenLists(
                        queueKey, fetchedKey);
                }

            } while (jobId == null);

            // The job was fetched by the server. To provide reliability,
            // we should ensure, that the job will be performed and acquired
            // resources will be disposed even if the server will crash 
            // while executing one of the subsequent lines of code.

            // The job's processing is splitted into a couple of checkpoints.
            // Each checkpoint occurs after successful update of the 
            // job information in the storage. And each checkpoint describes
            // the way to perform the job when the server was crashed after
            // reaching it.

            // Checkpoint #1-1. The job was fetched into the fetched list,
            // that is being inspected by the FetchedJobsWatcher instance.
            // Job's has the implicit 'Fetched' state.

            _redis.SetEntryInHash(
                String.Format(RedisStorage.Prefix + "job:{0}", jobId),
                "Fetched",
                JobHelper.ToStringTimestamp(DateTime.UtcNow));

            // Checkpoint #2. The job is in the implicit 'Fetched' state now.
            // This state stores information about fetched time. The job will
            // be re-queued when the JobTimeout will be expired.

            return new ProcessingJob(this, jobId, queueName);
        }

        public IDisposable AcquireJobLock(string jobId)
        {
            return _redis.AcquireLock(
                RedisStorage.Prefix + String.Format("job:{0}:state-lock", jobId),
                TimeSpan.FromMinutes(1));
        }

        public string CreateExpiredJob(
            InvocationData invocationData,
            string[] arguments,
            IDictionary<string, string> parameters, 
            TimeSpan expireIn)
        {
            var jobId = Guid.NewGuid().ToString();

            // Do not modify the original parameters.
            var storedParameters = new Dictionary<string, string>(parameters);
            storedParameters.Add("Type", invocationData.Type);
            storedParameters.Add("Method", invocationData.Method);
            storedParameters.Add("ParameterTypes", invocationData.ParameterTypes);
            storedParameters.Add("Arguments", JobHelper.ToJson(arguments));
            storedParameters.Add("CreatedAt", JobHelper.ToStringTimestamp(DateTime.UtcNow));

            using (var transaction = _redis.CreateTransaction())
            {
                transaction.QueueCommand(x => x.SetRangeInHash(
                    String.Format(RedisStorage.Prefix + "job:{0}", jobId),
                    storedParameters));

                transaction.QueueCommand(x => x.ExpireEntryIn(
                    String.Format(RedisStorage.Prefix + "job:{0}", jobId),
                    expireIn));

                // TODO: check return value
                transaction.Commit();
            }

            return jobId;
        }

        public JobData GetJobData(string id)
        {
            var storedData = _redis.GetAllEntriesFromHash(
                String.Format(RedisStorage.Prefix + "job:{0}", id));

            if (storedData.Count == 0) return null;

            string type = null;
            string method = null;
            string parameterTypes = null;

            if (storedData.ContainsKey("Type"))
            {
                type = storedData["Type"];
            }
            if (storedData.ContainsKey("Method"))
            {
                method = storedData["Method"];
            }
            if (storedData.ContainsKey("ParameterTypes"))
            {
                parameterTypes = storedData["ParameterTypes"];
            }

            MethodData methodData = null;
            JobLoadException loadException = null;

            var invocationData = new InvocationData(type, method, parameterTypes);

            try
            {
                methodData = MethodData.Deserialize(invocationData);
            }
            catch (JobLoadException ex)
            {
                loadException = ex;
            }
            
            return new JobData
            {
                MethodData = methodData,
                State = storedData.ContainsKey("State") ? storedData["State"] : null,
                Arguments = JobHelper.FromJson<string[]>(storedData.ContainsKey("Arguments") ? storedData["Arguments"] : null),
                LoadException = loadException
            };
        }

        public void SetJobParameter(string id, string name, string value)
        {
            _redis.SetEntryInHash(
                String.Format(RedisStorage.Prefix + "job:{0}", id),
                name,
                value);
        }

        public string GetJobParameter(string id, string name)
        {
            return _redis.GetValueFromHash(
                String.Format(RedisStorage.Prefix + "job:{0}", id),
                name);
        }

        public void DeleteJobFromQueue(string id, string queue)
        {
            RemoveFromFetchedList(_redis, queue, id);
        }

        public string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
        {
            return _redis.GetRangeFromSortedSetByLowestScore(
                RedisStorage.Prefix + key, fromScore, toScore, 0, 1)
                .FirstOrDefault();
        }

        public void AnnounceServer(string serverId, int workerCount, IEnumerable<string> queues)
        {
            using (var transaction = _redis.CreateTransaction())
            {
                transaction.QueueCommand(x => x.AddItemToSet(
                    RedisStorage.Prefix + "servers", serverId));

                transaction.QueueCommand(x => x.SetRangeInHash(
                    String.Format(RedisStorage.Prefix + "server:{0}", serverId),
                    new Dictionary<string, string>
                        {
                            { "WorkerCount", workerCount.ToString(CultureInfo.InvariantCulture) },
                            { "StartedAt", JobHelper.ToStringTimestamp(DateTime.UtcNow) },
                        }));

                foreach (var queue in queues)
                {
                    var queue1 = queue;
                    transaction.QueueCommand(x => x.AddItemToList(
                        String.Format(RedisStorage.Prefix + "server:{0}:queues", serverId),
                        queue1));
                }

                transaction.Commit();
            }
        }

        public void RemoveServer(string serverId)
        {
            RemoveServer(_redis, serverId);
        }

        public static void RemoveServer(IRedisClient redis, string serverId)
        {
            using (var transaction = redis.CreateTransaction())
            {
                transaction.QueueCommand(x => x.RemoveItemFromSet(
                    RedisStorage.Prefix + "servers",
                    serverId));

                transaction.QueueCommand(x => x.RemoveEntry(
                    String.Format(RedisStorage.Prefix + "server:{0}", serverId),
                    String.Format(RedisStorage.Prefix + "server:{0}:queues", serverId)));

                transaction.Commit();
            }
        }

        public void Heartbeat(string serverId)
        {
            _redis.SetEntryInHash(
                String.Format(RedisStorage.Prefix + "server:{0}", serverId),
                "Heartbeat",
                JobHelper.ToStringTimestamp(DateTime.UtcNow));
        }

        public int RemoveTimedOutServers(TimeSpan timeOut)
        {
            var serverNames = _redis.GetAllItemsFromSet(RedisStorage.Prefix + "servers");
            var heartbeats = new Dictionary<string, Tuple<DateTime, DateTime?>>();

            var utcNow = DateTime.UtcNow;

            using (var pipeline = _redis.CreatePipeline())
            {
                foreach (var serverName in serverNames)
                {
                    var name = serverName;

                    pipeline.QueueCommand(
                        x => x.GetValuesFromHash(
                            String.Format(RedisStorage.Prefix + "server:{0}", name),
                            "StartedAt", "Heartbeat"),
                        x => heartbeats.Add(
                            name,
                            new Tuple<DateTime, DateTime?>(
                                JobHelper.FromStringTimestamp(x[0]),
                                JobHelper.FromNullableStringTimestamp(x[1]))));
                }

                pipeline.Flush();
            }

            var removedServerCount = 0;
            foreach (var heartbeat in heartbeats)
            {
                var maxTime = new DateTime(
                    Math.Max(heartbeat.Value.Item1.Ticks, (heartbeat.Value.Item2 ?? DateTime.MinValue).Ticks));

                if (utcNow > maxTime.Add(timeOut))
                {
                    RemoveServer(_redis, heartbeat.Key);
                    removedServerCount++;
                }
            }

            return removedServerCount;
        }

        public static void RemoveFromFetchedList(
            IRedisClient redis,
            string queue,
            string jobId)
        {
            using (var transaction = redis.CreateTransaction())
            {
                transaction.QueueCommand(x => x.RemoveItemFromList(
                    String.Format(RedisStorage.Prefix + "queue:{0}:dequeued", queue),
                    jobId,
                    -1));

                transaction.QueueCommand(x => x.RemoveEntryFromHash(
                    String.Format(RedisStorage.Prefix + "job:{0}", jobId),
                    "Fetched"));
                transaction.QueueCommand(x => x.RemoveEntryFromHash(
                    String.Format(RedisStorage.Prefix + "job:{0}", jobId),
                    "Checked"));

                transaction.Commit();
            }
        }
    }
}