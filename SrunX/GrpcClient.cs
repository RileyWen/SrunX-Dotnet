using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using SlurmxGrpc;

namespace SrunX
{
    public class GrpcClient
    {
        public static bool TryAllocateResource(in AllocatableResource requiredResource, in string serverAddr,
            out ResourceInfo resourceInfo)
        {
            var channel = GrpcChannel.ForAddress(serverAddr);
            var client = new SlurmCtlXd.SlurmCtlXdClient(channel);

            var allocRequest = new ResourceAllocRequest {RequiredResource = requiredResource};
            var allocResult = client.AllocateResource(allocRequest);
            if (allocResult.Ok)
            {
                resourceInfo = allocResult.ResInfo;
                return true;
            }

            resourceInfo = null;
            log.Error($"Error occured while trying to allocate resource: {allocResult.Reason}");
            return false;
        }


        private enum SrunxStreamState
        {
            NegotiationWithSlurmxd,
            RequestNewTaskFromSlurmxd,
            WaitForNewTaskReply,
            WaitForIoRedirectionOrSignal,
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
                log.Error("Error while negotiation: stream cancelled");
                state = failedState;
                reply = null;
                return false;
            }

            reply = reader.Current;
            if (!expectedTypes.Contains(reply.Type))
            {
                log.Error(
                    $"Error while ReadNext: " +
                    $"Expect {string.Join(",", from elem in expectedTypes select elem.ToString())} " +
                    $"Got: {reply.Type.ToString()}");
                state = SrunxStreamState.Abort;
                return false;
            }

            return true;
        }

        public static bool ExecuteTask(in ResourceInfo resourceInfo, in string[] remoteCmd)
        {
            var channel = GrpcChannel.ForAddress($"http://{resourceInfo.Ipv4Addr}:{resourceInfo.Port}");
            var client = new SlurmXd.SlurmXdClient(channel);
            var stream = client.SrunXStream();

            var writer = stream.RequestStream;
            var reader = stream.ResponseStream;

            var state = new SrunxStreamState();
            state = SrunxStreamState.NegotiationWithSlurmxd;

            while (true)
            {
                switch (state)
                {
                    case SrunxStreamState.NegotiationWithSlurmxd:
                    {
                        var request = new SrunXStreamRequest
                        {
                            Type = SrunXStreamRequest.Types.Type.NegotiationRequest,
                            Negotiation = new NegotiationRequest {Version = GlobalConstants.SrunxVersion}
                        };

                        writer.WriteAsync(request);

                        if (!ReadNextAndCheckType(
                            reader, new[] {SrunXStreamReply.Types.Type.NegotiationReply},
                            ref state, SrunxStreamState.Abort, out var reply))
                            break;

                        if (!reply.NegotiationReply.Ok)
                            log.Error($"Error while negotiation: SlurmCtlXd: {reply.NegotiationReply.Reason}");


                        state = reply.NegotiationReply.Ok
                            ? SrunxStreamState.RequestNewTaskFromSlurmxd
                            : SrunxStreamState.Abort;
                        break;
                    }

                    case SrunxStreamState.RequestNewTaskFromSlurmxd:
                    {
                        var request = new SrunXStreamRequest
                        {
                            Type = SrunXStreamRequest.Types.Type.NewTask,
                            TaskInfo = new TaskInfo
                            {
                                ExecutivePath = remoteCmd[0],
                                Arguments = {remoteCmd[1..]},
                                ResourceUuid = resourceInfo.ResourceUuid
                            }
                        };

                        writer.WriteAsync(request);
                        state = SrunxStreamState.WaitForNewTaskReply;
                        break;
                    }

                    case SrunxStreamState.WaitForNewTaskReply:
                    {
                        if (!ReadNextAndCheckType(reader, new[] {SrunXStreamReply.Types.Type.NewTaskResult},
                            ref state, SrunxStreamState.Abort, out var reply))
                            break;

                        if (!reply.NewTaskResult.Ok)
                            log.Error($"Error while Executing the new task: SlurmCtlXd: {reply.NewTaskResult.Reason}");

                        state = reply.NewTaskResult.Ok
                            ? SrunxStreamState.WaitForIoRedirectionOrSignal
                            : SrunxStreamState.Abort;
                        break;
                    }

                    case SrunxStreamState.WaitForIoRedirectionOrSignal:
                    {
                        Task<SrunXStreamReply> readNextAsync = null;
                        Task waitCtrlCAsync = Task.Run(() => { CtrlCPressed.WaitOne(); });
                        while (true)
                        {
                            if (readNextAsync == null || readNextAsync.IsCompleted)
                                readNextAsync = Task.Run(() =>
                                {
                                    ReadNextAndCheckType(reader,
                                        new[]
                                        {
                                            SrunXStreamReply.Types.Type.IoRedirection,
                                            SrunXStreamReply.Types.Type.ExitStatus
                                        },
                                        ref state, SrunxStreamState.Abort, out var reply);
                                    return reply;
                                });

                            int index;
                            if (waitCtrlCAsync != null)
                                index = Task.WaitAny(readNextAsync, waitCtrlCAsync);
                            else
                                index = Task.WaitAny(readNextAsync);
                            if (index == 0)
                            {
                                var reply = readNextAsync.Result;
                                if (reply.Type == SrunXStreamReply.Types.Type.IoRedirection)
                                    Console.Write(reply.IoRedirection.Buf);
                                else if (reply.Type == SrunXStreamReply.Types.Type.ExitStatus)
                                {
                                    if (reply.TaskExitStatus.Reason == TaskExitStatus.Types.ExitReason.Normal)
                                        log.Info($"Task exited with value {reply.TaskExitStatus.Value}");
                                    else if (reply.TaskExitStatus.Reason == TaskExitStatus.Types.ExitReason.Signal)
                                        log.Info($"Task was killed by signal {reply.TaskExitStatus.Value}");
                                    state = SrunxStreamState.Finish;
                                    break;
                                }
                            }
                            else if (index == 1)
                            {
                                waitCtrlCAsync = null;
                                var request = new SrunXStreamRequest
                                {
                                    Type = SrunXStreamRequest.Types.Type.Signal,
                                    Signum = 2
                                };
                                writer.WriteAsync(request);
                            }
                        }

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

        public static void OnCtrlC(object sender, ConsoleCancelEventArgs e)
        {
            log.Debug("Ctrl+C Pressed.");
            CtrlCPressed.Set();
            e.Cancel = true;
        }

        private static ManualResetEvent CtrlCPressed = new ManualResetEvent(false);

        private static log4net.ILog log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType);
    }
}