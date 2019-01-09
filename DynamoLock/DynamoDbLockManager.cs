﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;

namespace DynamoLock
{
    public class DynamoDbLockManager : IDistributedLockManager
    {
        private readonly ILogger _logger;
        private readonly AmazonDynamoDBClient _client;
        private readonly ILockTableProvisioner _provisioner;
        private readonly string _tableName;
        private readonly string _nodeId;
        private readonly long _defaultLeaseTime = 30;
        private readonly TimeSpan _heartbeat = TimeSpan.FromSeconds(10);
        private readonly long _jitterTolerance = 1;
        private readonly List<string> _localLocks;
        private Task _heartbeatTask;
        private CancellationTokenSource _cancellationTokenSource;

        public DynamoDbLockManager(AWSCredentials credentials, AmazonDynamoDBConfig config, string tableName, ILockTableProvisioner provisioner, ILoggerFactory logFactory)
        {
            _logger = logFactory.CreateLogger<DynamoDbLockManager>();
            _client = new AmazonDynamoDBClient(credentials, config);
            _localLocks = new List<string>();
            _tableName = tableName;
            _provisioner = provisioner;
            _nodeId = Guid.NewGuid().ToString();
        }

        public DynamoDbLockManager(AWSCredentials credentials, AmazonDynamoDBConfig config, string tableName, ILockTableProvisioner provisioner, ILoggerFactory logFactory, long defaultLeaseTime, TimeSpan hearbeat)
            : this(credentials, config, tableName, provisioner, logFactory)
        {
            _defaultLeaseTime = defaultLeaseTime;
            _heartbeat = hearbeat;
        }

        public async Task<bool> AcquireLock(string Id)
        {
            try
            {
                var req = new PutItemRequest()
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        { "id", new AttributeValue(Id) },
                        { "lock_owner", new AttributeValue(_nodeId) },
                        {
                            "expires", new AttributeValue()
                            {
                                N = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() + _defaultLeaseTime)
                            }
                        },
                        {
                            "purge_time", new AttributeValue()
                            {
                                N = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() + (_defaultLeaseTime * 10))
                            }
                        }
                    },
                    ConditionExpression = "attribute_not_exists(id) OR (expires < :expired)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":expired", new AttributeValue()
                            {
                                N = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() + _jitterTolerance)
                            }
                        }
                    }
                };

                var response = await _client.PutItemAsync(req, _cancellationTokenSource.Token);

                if (response.HttpStatusCode == System.Net.HttpStatusCode.OK)
                {
                    _localLocks.Add(Id);
                    return true;
                }
            }
            catch (ConditionalCheckFailedException)
            {
            }
            return false;
        }

        public async Task ReleaseLock(string Id)
        {
            _localLocks.Remove(Id);
            try
            {
                var req = new DeleteItemRequest()
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "id", new AttributeValue(Id) }
                    },
                    ConditionExpression = "lock_owner = :node_id",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":node_id", new AttributeValue(_nodeId) }
                    }

                };
                await _client.DeleteItemAsync(req);
            }
            catch (ConditionalCheckFailedException)
            {
            }
        }

        public async Task Start()
        {
            await _provisioner.Provision();
            if (_heartbeatTask != null)
            {
                throw new InvalidOperationException();
            }

            _cancellationTokenSource = new CancellationTokenSource();

            _heartbeatTask = new Task(SendHeartbeat);
            _heartbeatTask.Start();
        }

        public Task Stop()
        {
            _cancellationTokenSource.Cancel();
            _heartbeatTask.Wait();
            _heartbeatTask = null;
            return Task.CompletedTask;
        }

        private async void SendHeartbeat()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_heartbeat, _cancellationTokenSource.Token);
                    foreach (var item in _localLocks)
                    {
                        var req = new PutItemRequest
                        {
                            TableName = _tableName,
                            Item = new Dictionary<string, AttributeValue>
                        {
                            { "id", new AttributeValue(item) },
                            { "lock_owner", new AttributeValue(_nodeId) },
                            {
                                "expires", new AttributeValue()
                                {
                                    N = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() + _defaultLeaseTime)
                                }
                            },
                            {
                                "purge_time", new AttributeValue()
                                {
                                    N = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() + (_defaultLeaseTime * 10))
                                }
                            }
                        },
                            ConditionExpression = "lock_owner = :node_id",
                            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            { ":node_id", new AttributeValue(_nodeId) }
                        }
                        };

                        await _client.PutItemAsync(req, _cancellationTokenSource.Token);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(default(EventId), ex, ex.Message);
                }
            }
        }
    }
}