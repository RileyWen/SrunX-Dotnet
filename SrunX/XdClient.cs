using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Task = System.Threading.Tasks.Task;
using SlurmxGrpc;

namespace SrunX
{
    public class XdClient
    {
        public XdClient()
        {
            CtrlCPressed = new ManualResetEvent(false);
            Console.CancelKeyPress += OnCtrlC;
            RejectCtrlC = false;
            TaskIsRunning = false;
        }

        private void OnCtrlC(object sender, ConsoleCancelEventArgs e)
        {
            if (!RejectCtrlC)
            {
                Log.Debug("Ctrl+C Pressed.");
                CtrlCPressed.Set();
            }
            else
            {
                Log.Debug("Ctrl+C has been pressed. Ignore it.");
            }

            e.Cancel = true;
        }

        private ManualResetEvent CtrlCPressed;
        private bool RejectCtrlC;
        private bool TaskIsRunning;

        private enum SrunxStreamState
        {
            Negotiation,
            CheckResource,
            ExecutiveInfo,
            WaitForIoRedirectionOrSignalOrTaskResult,
            Abort,
            Finish,
        }

        private static bool ReadNextAndCheckType(in IAsyncStreamReader<SrunXStreamReply> reader,
            in ICollection<SrunXStreamReply.Types.Type> expectedTypes, ref SrunxStreamState state,
            in SrunxStreamState failedState,
            out SrunXStreamReply reply)
        {
            var ok = reader.MoveNext().Result;
            if (!ok)
            {
                Log.Error("Error while ReadNext: stream cancelled");
                state = failedState;
                reply = null;
                return false;
            }

            reply = reader.Current;
            if (!expectedTypes.Contains(reply.Type))
            {
                Log.Error(
                    $"Error while ReadNext: " +
                    $"Expect {string.Join(",", from elem in expectedTypes select elem.ToString())} " +
                    $"Got: {reply.Type.ToString()}");
                state = SrunxStreamState.Abort;
                return false;
            }

            return true;
        }

        public bool ExecuteTask(in UInt32 taskId, in InteractiveTaskAllocationDetail allocationDetail,
            in string[] remoteCmd)
        {
            var channel = GrpcChannel.ForAddress($"http://{allocationDetail.Ipv4Addr}:{allocationDetail.Port}");
            var client = new SlurmXd.SlurmXdClient(channel);
            var stream = client.SrunXStream();

            var writer = stream.RequestStream;
            var reader = stream.ResponseStream;

            SrunxStreamState state;
            state = SrunxStreamState.Negotiation;

            while (true)
            {
                switch (state)
                {
                    case SrunxStreamState.Negotiation:
                    {
                        var request = new SrunXStreamRequest
                        {
                            Type = SrunXStreamRequest.Types.Type.NegotiationRequest,
                            Negotiation = new StreamRequestNegotiation { Version = GlobalConstants.SrunxVersion }
                        };

                        writer.WriteAsync(request);

                        if (!ReadNextAndCheckType(
                            reader, new[] { SrunXStreamReply.Types.Type.Result },
                            ref state, SrunxStreamState.Abort, out var reply))
                            break;

                        if (!reply.Result.Ok)
                            Log.Error($"Error while negotiation: SlurmCtlXd: {reply.Result.Reason}");


                        state = reply.Result.Ok
                            ? SrunxStreamState.CheckResource
                            : SrunxStreamState.Abort;
                        break;
                    }

                    case SrunxStreamState.CheckResource:
                    {
                        var request = new SrunXStreamRequest
                        {
                            Type = SrunXStreamRequest.Types.Type.CheckResource,
                            CheckResource = new StreamRequestCheckResource
                            {
                                TaskId = taskId,
                                ResourceUuid = allocationDetail.ResourceUuid
                            }
                        };
                        writer.WriteAsync(request);
                        if (!ReadNextAndCheckType(
                            reader, new[] { SrunXStreamReply.Types.Type.Result },
                            ref state, SrunxStreamState.Abort, out var reply))
                            break;

                        if (!reply.Result.Ok)
                            Log.Error($"Error while negotiation: SlurmCtlXd: {reply.Result.Reason}");

                        state = reply.Result.Ok ? SrunxStreamState.ExecutiveInfo : SrunxStreamState.Abort;
                        break;
                    }

                    case SrunxStreamState.ExecutiveInfo:
                    {
                        var request = new SrunXStreamRequest
                        {
                            Type = SrunXStreamRequest.Types.Type.ExecutiveInfo,
                            ExecInfo = new StreamRequestExecutiveInfo
                            {
                                ExecutivePath = remoteCmd[0],
                                Arguments = { remoteCmd[1..] },
                            }
                        };
                        writer.WriteAsync(request);
                        state = SrunxStreamState.WaitForIoRedirectionOrSignalOrTaskResult;

                        break;
                    }

                    case SrunxStreamState.WaitForIoRedirectionOrSignalOrTaskResult:
                    {
                        while (true)
                        {
                            Task waitCtrlCAsync = Task.Run(() => { CtrlCPressed.WaitOne(); });
                            Task<SrunXStreamReply> readNextAsync = Task.Run(() =>
                            {
                                ReadNextAndCheckType(reader,
                                    new[]
                                    {
                                        SrunXStreamReply.Types.Type.IoRedirection,
                                        SrunXStreamReply.Types.Type.ExitStatus,
                                        SrunXStreamReply.Types.Type.Result
                                    },
                                    ref state, SrunxStreamState.Abort, out var reply);
                                return reply;
                            });

                            int index = Task.WaitAny(readNextAsync, waitCtrlCAsync);
                            switch (index)
                            {
                                case 0:
                                {
                                    // When task is successfully created, IoRedirection may come earlier than NewTaskResult.
                                    var reply = readNextAsync.Result;
                                    switch (reply.Type)
                                    {
                                        case SrunXStreamReply.Types.Type.IoRedirection:
                                        {
                                            Console.Write(reply.Io.Buf);
                                            break;
                                        }
                                        case SrunXStreamReply.Types.Type.Result:
                                        {
                                            if (!reply.Result.Ok)
                                            {
                                                Log.Error(
                                                    $"Error while Executing the new task: SlurmCtlXd: {reply.Result.Reason}");
                                                state = SrunxStreamState.Abort;
                                                break;
                                            }

                                            TaskIsRunning = true;
                                            break;
                                        }
                                        case SrunXStreamReply.Types.Type.ExitStatus:
                                        {
                                            if (reply.ExitStatus.Reason ==
                                                StreamReplyExitStatus.Types.ExitReason.Normal)
                                                Log.Info($"Task exited with value {reply.ExitStatus.Value}");
                                            else if (reply.ExitStatus.Reason ==
                                                     StreamReplyExitStatus.Types.ExitReason.Signal)
                                                Log.Info($"Task was killed by signal {reply.ExitStatus.Value}");
                                            state = SrunxStreamState.Finish;
                                            goto StreamExit;
                                        }
                                    }

                                    break;
                                }
                                case 1:
                                {
                                    CtrlCPressed.Reset();

                                    if (TaskIsRunning)
                                    {
                                        RejectCtrlC = true;
                                        var request = new SrunXStreamRequest
                                        {
                                            Type = SrunXStreamRequest.Types.Type.Signal,
                                            Signum = 2
                                        };
                                        writer.WriteAsync(request);
                                    }

                                    break;
                                }
                            }
                        }

                        StreamExit:
                        break;
                    }

                    case SrunxStreamState.Abort:
                        writer.CompleteAsync().Wait();

                        return false;
                    case SrunxStreamState.Finish:
                        writer.CompleteAsync().Wait();

                        return true;
                    default:
                        throw new ArgumentOutOfRangeException($"Unknown srunx state: {state}");
                }
            }
        }

        private static readonly log4net.ILog Log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType);
    }
}