using Google.Protobuf.Collections;
using Grpc.Core;
using Spark.Connect.Dotnet.Sql;

namespace Spark.Connect.Dotnet.Grpc;

/*
 * When we connect to remote spark clusters, including Databricks we can find our tcp connections are closed
 *  Azure has a 5 minute idle time where connections are killed and Databricks has a hard 1 hour timeout so we
 *  need to create the initial request with the Reattach option Reattachable set to true and then we can re-connect
 *  a failed connection using ReattachExecute so we don't have to re-run the query from the beginning.
 *
 *  The .NET gRPC request will sit forever and not respond if the connection is killed so we use a scheduled cancellation
 *  to cancel the request if we haven't had a response in 1 minute.
 *
 *  When we get the first response it includes an operation id and a request id - we use the operation id to identify the query run
 *   and the response id to track the responses we have received. the response id is like a pointer to tell the server where we have
 *   received the response up to.
 *
 *  When we finish and have everything we need we should tell the server to release the responses so the memory is freed on the server.
 */
public class RequestExecutor : IDisposable
{
    private readonly SparkSession _session;
    private readonly Plan _plan;
    private readonly GrpcLogger _logger;
    
    private string _operationId = string.Empty;
    private string _lastResponseId = string.Empty;
    private bool _isComplete = false;
    
    private CancellationTokenSource _currentCancellationSource = new ();

    private Relation? _relation;
    private DataType? _schema;
    private readonly List<Row> _rows = new ();
    private StreamingQueryInstanceId? _streamingQueryId;
    private StreamingQueryCommandResult.Types.StatusResult? _streamingResultStatus;
    private string? _streamingQueryName;
    private bool? _streamingQueryIsTerminated = false;
    private StreamingQueryCommandResult.Types.ExceptionResult? _streamingQueryException;
    private StreamingQueryCommandResult.Types.RecentProgressResult? _streamingProgress;

    private enum RetryableState
    {
        Network,
        Processing
    }

    private RetryableState _retryableState = RetryableState.Processing;
    
    public RequestExecutor(SparkSession session, Plan plan)
    {
        _logger = GetLogger(session);
        _session = session;
        _plan = plan;

        _relation = plan.Root;
    }

    private GrpcLogger GetLogger(SparkSession session)
    {
        if (session.Conf.SparkDotnetConnectOptions.TryGetValue(SparkDotnetKnownConfigKeys.GrpcLogging, out string? logging))
        {
            if (logging == "console")
            {
                return new GrpcLogger(GrpcLoggingLevel.Verbose, session.Console);
            }
        }

        return new GrpcNullLogger(GrpcLoggingLevel.None, null);
    }

    public void Exec()
    {
        var task = Task.Run(ExecAsync);
        task.Wait();
    }
    
    public async Task ExecAsync()
    {
        var shouldContinue = true;
        
        while (shouldContinue && !_isComplete)
        {
            shouldContinue = await ProcessRequest();
            _logger.Log(GrpcLoggingLevel.Verbose, $" Processed Request, continue?: {shouldContinue}");
        }
    }
    
    private CancellationToken GetScheduledCancellationToken()
    {
        _currentCancellationSource = new CancellationTokenSource();
        _currentCancellationSource.CancelAfter(TimeSpan.FromMinutes(1));
        var token = _currentCancellationSource.Token;
        return token;
    }
    
    private async Task<bool> ProcessRequest()
    {
        _logger.Log(GrpcLoggingLevel.Verbose, $" Processing Request");
        
        try
        {
            _retryableState = RetryableState.Network;
            var response = GetResponse();
            await response.ResponseStream.MoveNext();
            _retryableState = RetryableState.Processing;
            
            while (response.ResponseStream.Current != null)
            {
                var current = response.ResponseStream.Current;

                if (current.ResultComplete != null)
                {
                    _isComplete = true;
                }

                if (current.OperationId != null)
                {
                    _operationId = current.OperationId;
                }

                if (current.SqlCommandResult != null)
                {
                    _logger.Log(GrpcLoggingLevel.Verbose, $"SqlCommandResult: {current.SqlCommandResult.Relation}");
                    _relation = current.SqlCommandResult.Relation;
                }

                if (current.Schema != null)
                {
                    _logger.Log(GrpcLoggingLevel.Verbose, $"schema: {current.Schema}");
                    _schema = current.Schema;
                }

                if (current.ArrowBatch != null)
                {
                    _logger.Log(GrpcLoggingLevel.Verbose, "Have Arrow Batch");
                    var wrapper = new ArrowWrapper();

                    if (_schema == null)
                    {
                        _logger.Log(GrpcLoggingLevel.Verbose, "Cannot decode arrow batch as schema is null");
                    }
                    else
                    {
                        if (!_session.Conf.IsTrue(SparkDotnetKnownConfigKeys.DontDecodeArrow))
                        {
                            _rows.AddRange(await wrapper.ArrowBatchToRows(current.ArrowBatch, _schema));    
                        }
                        else
                        {
                            _logger.Log(GrpcLoggingLevel.Verbose, "Not decoding Arrow as DontDecodeArrow is true");
                        }
                    }
                }

                if (current.Metrics != null)
                {
                    _logger.Log(GrpcLoggingLevel.Verbose, "Have Metrics");
                    PrintMetrics(current.Metrics);
                }

                if (current.ObservedMetrics != null && current.ObservedMetrics.Count > 0)
                {
                    _logger.Log(GrpcLoggingLevel.Verbose, "Have observed metrics");
                    PrintObservedMetrics(current.ObservedMetrics);
                }
                
                if (current.StreamingQueryCommandResult != null)
                {
                    _streamingQueryId = current.StreamingQueryCommandResult.QueryId;
                    _streamingResultStatus = current.StreamingQueryCommandResult.Status;
                }
                
                if (current.WriteStreamOperationStartResult != null)
                {
                    _streamingQueryId = current.WriteStreamOperationStartResult.QueryId;
                    _streamingQueryName = current.WriteStreamOperationStartResult.Name;
                }
                
                if (current.StreamingQueryCommandResult is { AwaitTermination: not null })
                {
                    _streamingQueryIsTerminated = current.StreamingQueryCommandResult.AwaitTermination.Terminated;
                }
                
                if (current.StreamingQueryCommandResult is { Exception: not null })
                {
                    _streamingQueryException = current.StreamingQueryCommandResult.Exception;
                }
            
                if (current.StreamingQueryCommandResult is {RecentProgress: not null})
                {
                    _streamingProgress = current.StreamingQueryCommandResult.RecentProgress;
                }
                
                //ResponseId always has to come last because it is the marker to tell the server
                // where we are if we get disconnected, if we haven't finished reading the response 
                // then we can ask for it again (_lastResponseId)
                if (current.ResponseId != null)
                {
                    _lastResponseId = current.ResponseId;
                    _logger.Log(GrpcLoggingLevel.Verbose, $" Received Response Id: {_lastResponseId}");
                }

                await response.ResponseStream.MoveNext();
            }
        }
        catch (RpcException r)
        {
            if (r.Status.StatusCode == StatusCode.Cancelled)    //This is a client side cancelled
            {
                _logger.Log(GrpcLoggingLevel.Warn, "Request was cancelled aka timed out - retrying: {0}", r.Message);
                return true;
            }
            
            if (r.Status.Detail.Contains("SPARK_JOB_CANCELLED")) //Server side "kill"
            {
                _logger.Log(GrpcLoggingLevel.Warn, "Request was killed from the server {0}", r.Status.Detail);
                throw;
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.Log(GrpcLoggingLevel.Warn, "Exception in ExecRequest: {0}", ex.Message);
            if (ex.Message.Contains("SPARK_JOB_CANCELLED"))
            {
                _logger.Log(GrpcLoggingLevel.Warn, "Request was killed from the server {0}", ex.Message);
                throw;
            }

            if (_retryableState == RetryableState.Processing)
            {
                throw; 
            }
        }

        return true;
    }
    
    private AsyncServerStreamingCall<ExecutePlanResponse> GetResponse()
    {
        if (_operationId == string.Empty)
        { 
            var request = CreateRequest();
            _logger.Log(GrpcLoggingLevel.Verbose, "Calling Execute Plan on session {0}", _session.SessionId);
            return _session.GrpcClient.ExecutePlan(request, _session.Headers, null, GetScheduledCancellationToken());
        }
        else
        {
            var request = CreateReattachRequest();
            _logger.Log(GrpcLoggingLevel.Verbose, "Calling ReattachExecute Plan on session {0}", _session.SessionId);
            return _session.GrpcClient.ReattachExecute(request, _session.Headers, null, GetScheduledCancellationToken());
        }
    }
    
    private ExecutePlanRequest CreateRequest() => new()
    {
        Plan = _plan, ClientType = _session.ClientType, SessionId = _session.SessionId, UserContext = _session.UserContext, RequestOptions =
        {
            new ExecutePlanRequest.Types.RequestOption()
            {
                ReattachOptions = new ReattachOptions()
                {
                    Reattachable = true
                }
            }
        }
    };

    private ReattachExecuteRequest CreateReattachRequest() => new()
    {
        ClientType = _session.ClientType, SessionId = _session.SessionId, UserContext = _session.UserContext, OperationId = _operationId, LastResponseId = _lastResponseId
    };

    private ReleaseExecuteRequest CreateReleaseRequest() => new()
    {
        ClientType = _session.ClientType, SessionId = _session.SessionId, UserContext = _session.UserContext, OperationId = _operationId, ReleaseUntil = new ReleaseExecuteRequest.Types.ReleaseUntil(){ResponseId = _lastResponseId}
    };

    private void PrintMetrics(ExecutePlanResponse.Types.Metrics currentMetrics)
    {
        if (_session.Conf.SparkDotnetConnectOptions.TryGetValue(SparkDotnetKnownConfigKeys.PrintMetrics, out string? logging))
        {
            if (logging == "true")
            {
                foreach (var metric in currentMetrics.Metrics_)
                foreach (var value in metric.ExecutionMetrics)
                {
                    _session.Console.WriteLine(
                        $"metric: {metric.Name}, parent: {metric.Parent} planid: {metric.PlanId}, {value.Key} = {value.Value}");
                }
            }
        }
    }
    
    private void PrintObservedMetrics(RepeatedField<ExecutePlanResponse.Types.ObservedMetrics> currentObservedMetrics)
    {
        if (_session.Conf.SparkDotnetConnectOptions.TryGetValue(SparkDotnetKnownConfigKeys.PrintMetrics, out string? logging))
        {
            if (logging == "true")
            {
                foreach (var metric in currentObservedMetrics)
                {
                    for (var i = 0; i < metric.Keys.Count; i++)
                    {
                        _session.Console.WriteLine(
                            $"observed metric: {metric.Name}, {metric.PlanId}, {metric.Keys[i]} = {metric.Values[i]}");
                    }
                }
            }
        }
        
    }
    
    public void Dispose()
    {
        if (_operationId != string.Empty && _lastResponseId != String.Empty)
        {
            Task.Run(() =>
            {
                var releaseRequest = CreateReleaseRequest();
                var response = _session.GrpcClient.ReleaseExecute(releaseRequest, _session.Headers);
                _logger.Log(GrpcLoggingLevel.Verbose
                    , $"Releases Session: {_session.SessionId}, Operation ID: {_operationId}, Up to Response: {_lastResponseId}, response server side id: {response.ServerSideSessionId}");
            });
        }
    }

    public IList<Row> GetData() => _rows;

    public DataType GetSchema() => _schema!;

    public Relation GetRelation() => _relation!;

    public StreamingQueryInstanceId GetStreamingQueryId() => _streamingQueryId!;

    public StreamingQueryCommandResult.Types.StatusResult GetStreamingQueryCommandResult() => _streamingResultStatus!;

    public string GetStreamingQueryName() => _streamingQueryName!;

    public bool GetStreamingQueryIsTerminated() => _streamingQueryIsTerminated!.Value;

    public StreamingQueryCommandResult.Types.ExceptionResult? GetStreamingException() => _streamingQueryException;

    public StreamingQueryCommandResult.Types.RecentProgressResult? GetStreamingRecentProgress() => _streamingProgress;
}
