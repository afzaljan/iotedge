// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Docker.Commands
{
    using System;
    using System.Collections.Generic;
    // ReSharper disable once RedundantUsingDirective
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Docker.DotNet;
    using global::Docker.DotNet.Models;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Configuration;
    using Newtonsoft.Json;
    

    public class CreateCommand : ICommand
    {
        readonly CreateContainerParameters createContainerParameters;
        readonly IDockerClient client;
        readonly static Dictionary<string, PortBinding> EdgeHubPortBinding = new Dictionary<string, PortBinding>
        {
            {"8883/tcp", new PortBinding {HostPort="8883" } },
            {"443/tcp", new PortBinding {HostPort="443" } }
        };

        public CreateCommand(IDockerClient client, CreateContainerParameters createContainerParameters)
        {
            this.client = Preconditions.CheckNotNull(client, nameof(client));
            this.createContainerParameters = Preconditions.CheckNotNull(createContainerParameters, nameof(createContainerParameters));
        }

        public static async Task<ICommand> BuildAsync(
            IDockerClient client,
            DockerModule module,
            IModuleIdentity identity,
            DockerLoggingConfig defaultDockerLoggerConfig,
            IConfigSource configSource,
            bool buildForEdgeHub
        )
        {
            // Validate parameters
            Preconditions.CheckNotNull(client, nameof(client));
            Preconditions.CheckNotNull(module, nameof(module));
            Preconditions.CheckNotNull(defaultDockerLoggerConfig, nameof(defaultDockerLoggerConfig));
            Preconditions.CheckNotNull(configSource, nameof(configSource));

            CreateContainerParameters createContainerParameters = module.Config.CreateOptions ?? new CreateContainerParameters();

            // serialize user provided create options to add as docker label, before adding other values
            string createOptionsString = JsonConvert.SerializeObject(createContainerParameters);

            // Force update parameters with indexing entries
            createContainerParameters.Name = module.Name;
            createContainerParameters.Image = module.Config.Image;

            DeploymentConfigInfo deploymentConfigInfo = await configSource.GetDeploymentConfigInfoAsync();
            DeploymentConfig deploymentConfig = deploymentConfigInfo.DeploymentConfig;
            Option<DockerRuntimeInfo> dockerRuntimeInfo = deploymentConfig != DeploymentConfig.Empty && deploymentConfig.Runtime is DockerRuntimeInfo
                ? Option.Some(deploymentConfig.Runtime as DockerRuntimeInfo)
                : Option.None<DockerRuntimeInfo>();

            // Inject global parameters
            InjectCerts(createContainerParameters, configSource, buildForEdgeHub);
            InjectConfig(createContainerParameters, identity, buildForEdgeHub);
            InjectPortBindings(createContainerParameters, buildForEdgeHub);
            InjectLoggerConfig(createContainerParameters, defaultDockerLoggerConfig, dockerRuntimeInfo.Map(r => r.Config.LoggingOptions));

            // Inject required Edge parameters
            InjectLabels(createContainerParameters, module, createOptionsString);

            InjectNetworkAlias(createContainerParameters, configSource, buildForEdgeHub);

            return new CreateCommand(client, createContainerParameters);
        }

        public Task ExecuteAsync(CancellationToken token) => this.client.Containers.CreateContainerAsync(this.createContainerParameters, token);

        public string Show() => $"docker create {ObfuscateConnectionStringInCreateContainerParameters(JsonConvert.SerializeObject(this.createContainerParameters))}";

        public Task UndoAsync(CancellationToken token) => TaskEx.Done;

        static void InjectConfig(CreateContainerParameters createContainerParameters, IModuleIdentity identity, bool injectForEdgeHub)
        {
            // Inject the connection string as an environment variable
            if (!string.IsNullOrWhiteSpace(identity.ConnectionString))
            {
                string connectionStringKey = injectForEdgeHub ? Constants.IotHubConnectionStringKey : Constants.EdgeHubConnectionStringKey;
                var envVars = new List<string>()
                {
                    $"{connectionStringKey}={identity.ConnectionString}"
                };
                if (injectForEdgeHub)
                {
                    envVars.Add($"{Logger.RuntimeLogLevelEnvKey}={Logger.GetLogLevel()}");
                }

                InjectEnvVars(createContainerParameters, envVars);
            }
        }

        static void InjectPortBindings(CreateContainerParameters createContainerParameters, bool injectForEdgeHub)
        {
            if (injectForEdgeHub)
            {
                createContainerParameters.HostConfig = createContainerParameters.HostConfig ?? new HostConfig();
                createContainerParameters.HostConfig.PortBindings = createContainerParameters.HostConfig.PortBindings ?? new Dictionary<string, IList<PortBinding>>();

                foreach (KeyValuePair<string, PortBinding> binding in EdgeHubPortBinding)
                {
                    IList<PortBinding> current = createContainerParameters.HostConfig.PortBindings.GetOrElse(binding.Key, () => new List<PortBinding>());
                    current.Add(binding.Value);
                    createContainerParameters.HostConfig.PortBindings[binding.Key] = current;
                }
            }
        }

        static void InjectLoggerConfig(CreateContainerParameters createContainerParameters, DockerLoggingConfig defaultDockerLoggerConfig, Option<string> sourceLoggingOptions)
        {
            createContainerParameters.HostConfig = createContainerParameters.HostConfig ?? new HostConfig();

            Option<LogConfig> sourceOptions;
            try
            {
                sourceOptions = sourceLoggingOptions.Filter(l => !string.IsNullOrEmpty(l)).Map(l =>
                    JsonConvert.DeserializeObject<LogConfig>(l));
            }
            catch
            {
                sourceOptions = Option.None<LogConfig>();
            }

            if ((createContainerParameters.HostConfig.LogConfig == null) || (string.IsNullOrWhiteSpace(createContainerParameters.HostConfig.LogConfig.Type)))
            {
                createContainerParameters.HostConfig.LogConfig = sourceOptions.GetOrElse(new LogConfig
                {
                    Type = defaultDockerLoggerConfig.Type,
                    Config = defaultDockerLoggerConfig.Config
                });
            }
        }

        static void InjectLabels(CreateContainerParameters createContainerParameters, DockerModule module, string createOptionsString)
        {
            // Inject required Edge parameters
            createContainerParameters.Labels = createContainerParameters.Labels ?? new Dictionary<string, string>();

            createContainerParameters.Labels[Constants.Labels.Owner] = Constants.OwnerValue;
            createContainerParameters.Labels[Constants.Labels.NormalizedCreateOptions] = createOptionsString;
            createContainerParameters.Labels[Constants.Labels.RestartPolicy] = module.RestartPolicy.ToString();
            createContainerParameters.Labels[Constants.Labels.DesiredStatus] = module.DesiredStatus.ToString();

            if (!string.IsNullOrWhiteSpace(module.Version))
            {
                createContainerParameters.Labels[Constants.Labels.Version] = module.Version;
            }

            if (!string.IsNullOrWhiteSpace(module.ConfigurationInfo.Id))
            {
                createContainerParameters.Labels[Constants.Labels.ConfigurationId] = module.ConfigurationInfo.Id;
            }
        }

        static void InjectNetworkAlias(CreateContainerParameters createContainerParameters, IConfigSource configSource, bool addEdgeDeviceHostNameAlias)
        {
            string networkId = configSource.Configuration.GetValue<string>(Docker.Constants.NetworkIdKey);
            string edgeDeviceHostName = configSource.Configuration.GetValue<string>(Constants.EdgeDeviceHostNameKey);
            if (!string.IsNullOrWhiteSpace(networkId))
            {
                var endpointSettings = new EndpointSettings();
                if (addEdgeDeviceHostNameAlias && !string.IsNullOrWhiteSpace(edgeDeviceHostName))
                {
                    endpointSettings.Aliases = new List<string> { edgeDeviceHostName };
                }

                IDictionary<string, EndpointSettings> endpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkId] = endpointSettings
                };
                createContainerParameters.NetworkingConfig = new NetworkingConfig { EndpointsConfig = endpointsConfig };
            }
        }

        static void InjectVolume(CreateContainerParameters createContainerParameters, string volumeName, string volumePath, bool readOnly = true)
        {
            if (!string.IsNullOrWhiteSpace(volumeName) && !string.IsNullOrWhiteSpace(volumePath))
            {
                HostConfig hostConfig = createContainerParameters.HostConfig = createContainerParameters.HostConfig ?? new HostConfig();
                hostConfig.Binds = hostConfig.Binds ?? new List<string>();

                string ro = readOnly ? ":ro" : string.Empty;
                hostConfig.Binds.Add($"{volumeName}:{volumePath}{ro}");
            }
        }

        static void InjectCerts(CreateContainerParameters createContainerParameters, IConfigSource configSource, bool injectForEdgeHub)
        {
            createContainerParameters.HostConfig = createContainerParameters.HostConfig ?? new HostConfig();
            var varsList = new List<string>();
            if (injectForEdgeHub)
            {
                // for the EdgeHub we need to inject the CA chain cert that was used to sign the Hub server certificate
                string moduleCaChainCertFile = configSource.Configuration.GetValue(Constants.EdgeModuleHubServerCaChainCertificateFileKey, string.Empty);
                if (string.IsNullOrWhiteSpace(moduleCaChainCertFile) == false)
                {
                    varsList.Add($"{Constants.EdgeModuleHubServerCaChainCertificateFileKey}={moduleCaChainCertFile}");
                }

                // for the EdgeHub we also need to inject the Hub server certificate which will be used for TLS connections
                // from modules and leaf devices
                string moduleHubCertFile = configSource.Configuration.GetValue(Constants.EdgeModuleHubServerCertificateFileKey, string.Empty);
                if (string.IsNullOrWhiteSpace(moduleHubCertFile) == false)
                {
                    varsList.Add($"{Constants.EdgeModuleHubServerCertificateFileKey}={moduleHubCertFile}");
                }

                // mount edge hub volume
                InjectVolume(
                    createContainerParameters,
                    configSource.Configuration.GetValue(Constants.EdgeHubVolumeNameKey, string.Empty),
                    configSource.Configuration.GetValue(Constants.EdgeHubVolumePathKey, string.Empty)
                );
            }
            else
            {
                // for all Edge modules, the agent should inject the CA certificate that can be used for Edge Hub server certificate
                // validation
                string moduleCaCertFile = configSource.Configuration.GetValue(Constants.EdgeModuleCaCertificateFileKey, string.Empty);
                if (string.IsNullOrWhiteSpace(moduleCaCertFile) == false)
                {
                    varsList.Add($"{Constants.EdgeModuleCaCertificateFileKey}={moduleCaCertFile}");
                }

                // mount module volume
                InjectVolume(
                    createContainerParameters,
                    configSource.Configuration.GetValue(Constants.EdgeModuleVolumeNameKey, string.Empty),
                    configSource.Configuration.GetValue(Constants.EdgeModuleVolumePathKey, string.Empty)
                );
            }

            InjectEnvVars(createContainerParameters, varsList);
        }

        static void InjectEnvVars(
            CreateContainerParameters createContainerParameters,
            IList<string> varsList
        )
        {
            createContainerParameters.Env = createContainerParameters.Env?.RemoveIntersectionKeys(varsList).ToList() ?? new List<string>();
            foreach(string envVar in varsList)
            {
                createContainerParameters.Env.Add(envVar);
            }
        }

        static string ObfuscateConnectionStringInCreateContainerParameters(string serializedCreateOptions)
        {
            var scrubbed = JsonConvert.DeserializeObject<CreateContainerParameters>(serializedCreateOptions);
            scrubbed.Env = scrubbed.Env?
                .Select((env, i) => env.IndexOf(Constants.EdgeHubConnectionStringKey, StringComparison.Ordinal) == -1 ? env : $"{Constants.EdgeHubConnectionStringKey}=******")
                .Select((env, i) => env.IndexOf(Constants.IotHubConnectionStringKey, StringComparison.Ordinal) == -1 ? env : $"{Constants.IotHubConnectionStringKey}=******")
                .ToList();
            return JsonConvert.SerializeObject(scrubbed);
        }
    }
}
