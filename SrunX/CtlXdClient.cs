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
    public class CtlXdClient
    {
        private GrpcChannel mCtlXdChannel;
        private SlurmCtlXd.SlurmCtlXdClient mCtlXdClient;

        public CtlXdClient(string serverAddress)
        {
            mCtlXdChannel = GrpcChannel.ForAddress(serverAddress);
            mCtlXdClient = new SlurmCtlXd.SlurmCtlXdClient(mCtlXdChannel);
        }

        public (bool ok, uint? taskId) AllocateInteractiveTask(
            in string partitionName,
            in Resources requiredResource,
            UInt64 timeLimitSec)
        {
            var allocRequest = new InteractiveTaskAllocRequest
                { RequiredResources = requiredResource, PartitionName = partitionName, TimeLimitSec = timeLimitSec };
            var allocResult = mCtlXdClient.AllocateInteractiveTask(allocRequest);
            if (allocResult.Ok)
            {
                return (true, allocResult.TaskId);
            }

            Log.Error($"Error occured while trying to allocate resource: {allocResult.Reason}");
            return (false, null);
        }

        public (bool ok, InteractiveTaskAllocationDetail allocationDetail) QueryInteractiveTaskAllocDetail(
            UInt32 taskId)
        {
            var queryRequest = new QueryInteractiveTaskAllocDetailRequest { TaskId = taskId };
            var queryReply = mCtlXdClient.QueryInteractiveTaskAllocDetail(queryRequest);
            if (queryReply.Ok)
            {
                return (true, queryReply.Detail);
            }

            return (false, null);
        }

        private static readonly log4net.ILog Log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType);
    }
}