﻿using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure;
using Microsoft.Azure.Management.Resources;
using Microsoft.Azure.Management.Resources.Models;

using Microsoft.Deployment.Common;
using Microsoft.Deployment.Common.ActionModel;
using Microsoft.Deployment.Common.Actions;
using Microsoft.Deployment.Common.ErrorCode;
using Microsoft.Deployment.Common.Helpers;

namespace Microsoft.Deployment.Actions.AzureCustom.IoTContinuousDataExport
{
    [Export(typeof(IAction))]
    public class DeployIoTCDEAzureFunction : BaseAction
    {
        public override async Task<ActionResponse> ExecuteActionAsync(ActionRequest request)
        {
            string idSubscription = request.DataStore.GetJson("SelectedSubscription", "SubscriptionId");
            string nameResourceGroup = request.DataStore.GetValue("SelectedResourceGroup");
            string tokenAzure = request.DataStore.GetJson("AzureToken", "access_token");

            string databaseConnectionString = request.DataStore.GetValue("SqlConnectionString");
            string storageAccountConnectionString = request.DataStore.GetJson("SelectedStorageAccount", "StorageAccountConnectionString");
            string functionName = request.DataStore.GetValue("functionName");
            string functionStorageAccountName = request.DataStore.GetValue("functionStorageAccountName");

            string deploymentName = request.DataStore.GetValue("DeploymentName") ?? request.DataStore.CurrentRoute;

            var payload = new AzureArmParameterGenerator();

            var armParameters = request.DataStore.GetJson("AzureArmParameters");
            if (armParameters != null)
            {
                foreach (var prop in armParameters.Children())
                {
                    string key = prop.Path.Split('.').Last();
                    string value = prop.First().ToString();

                    payload.AddStringParam(key, value);
                }
            }

            payload.AddStringParam("siteName", functionName);
            payload.AddStringParam("blobStorageConnectionString", storageAccountConnectionString);
            payload.AddStringParam("functionStorageAccountName", functionStorageAccountName);
            payload.AddStringParam("databaseConnectionString", databaseConnectionString);

            var armTemplate = JsonUtility.GetJObjectFromJsonString(File.ReadAllText(Path.Combine(request.Info.App.AppFilePath, request.DataStore.GetValue("AzureArmFile"))));
            var armParamTemplate = JsonUtility.GetJObjectFromObject(payload.GetDynamicObject());
            armTemplate.Remove("parameters");
            armTemplate.Add("parameters", armParamTemplate["parameters"]);

            var deployment = new Microsoft.Azure.Management.Resources.Models.Deployment()
            {
                Properties = new DeploymentPropertiesExtended()
                {
                    Template = armTemplate.ToString(),
                    Parameters = JsonUtility.GetEmptyJObject().ToString()
                }
            };

            ResourceManagementClient client = new ResourceManagementClient(new TokenCloudCredentials(idSubscription, tokenAzure));
            var validate = await client.Deployments.ValidateAsync(nameResourceGroup, deploymentName, deployment, new CancellationToken());
            if (!validate.IsValid)
            {
                return new ActionResponse(ActionStatus.Failure, JsonUtility.GetJObjectFromObject(validate), null, DefaultErrorCodes.DefaultErrorCode,
                    $"Azure:{validate.Error.Message} Details:{validate.Error.Details}");
            }

            var deploymentItem = await client.Deployments.CreateOrUpdateAsync(nameResourceGroup, deploymentName, deployment, new CancellationToken());
            ActionResponse r = await WaitForAction(client, nameResourceGroup, deploymentName);

            request.DataStore.AddToDataStore("ArmOutput", r.DataStore.GetValue("ArmOutput"), DataStoreType.Public);
            return r;
        }

        private static async Task<ActionResponse> WaitForAction(ResourceManagementClient client, string resourceGroup, string deploymentName)
        {
            for (; ; )
            {
                Thread.Sleep(Constants.ACTION_WAIT_INTERVAL);
                var status = await client.Deployments.GetAsync(resourceGroup, deploymentName, new CancellationToken());
                var operations = await client.DeploymentOperations.ListAsync(resourceGroup, deploymentName, new DeploymentOperationsListParameters(), new CancellationToken());
                var provisioningState = status.Deployment.Properties.ProvisioningState;

                if (provisioningState == "Accepted" || provisioningState == "Running")
                    continue;

                if (provisioningState == "Succeeded")
                {
                    ActionResponse r = new ActionResponse(ActionStatus.Success, operations) { DataStore = new DataStore() };
                    string outputs = status.Deployment.Properties.Outputs;
                    if (!string.IsNullOrEmpty(outputs))
                    {
                        r.DataStore.AddToDataStore("ArmOutput", outputs, DataStoreType.Public);
                    }

                    return r;
                }

                var operation = operations.Operations.First(p => p.Properties.ProvisioningState == ProvisioningState.Failed);
                var operationFailed = await client.DeploymentOperations.GetAsync(resourceGroup, deploymentName, operation.OperationId, new CancellationToken());

                return new ActionResponse(ActionStatus.Failure, operationFailed);
            }
        }
    }
}