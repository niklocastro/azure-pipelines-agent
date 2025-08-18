// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Agent.Sdk.Util;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.PluginHost
{
    public static class Program
    {
        private static CancellationTokenSource tokenSource = new CancellationTokenSource();
        private static string executingAssemblyLocation = string.Empty;

        public static int Main(string[] args)
        {
            if (PlatformUtil.UseLegacyHttpHandler)
            {
                AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);
            }

            Console.CancelKeyPress += Console_CancelKeyPress;

            // Set encoding to UTF8, process invoker will use UTF8 write to STDIN
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                ArgUtil.NotNull(args, nameof(args));
                ArgUtil.Equal(2, args.Length, nameof(args.Length));

                string pluginType = args[0];
                if (string.Equals("task", pluginType, StringComparison.OrdinalIgnoreCase))
                {
                    string assemblyQualifiedName = args[1];
                    ArgUtil.NotNullOrEmpty(assemblyQualifiedName, nameof(assemblyQualifiedName));

                    string serializedContext = Console.ReadLine();
                    ArgUtil.NotNullOrEmpty(serializedContext, nameof(serializedContext));

                    AgentTaskPluginExecutionContext executionContext = null;
                    try
                    {
                        executionContext = StringUtil.ConvertFromJson<AgentTaskPluginExecutionContext>(serializedContext);
                    }
                    catch (Exception jsonEx)
                    {
                        // Malformed JSON or unexpected payload
                        Console.Error.WriteLine($"Failed to parse task plugin context: {jsonEx.Message}");
                        return 1;
                    }
                    ArgUtil.NotNull(executionContext, nameof(executionContext));

                    VariableValue culture;
                    ArgUtil.NotNull(executionContext.Variables, nameof(executionContext.Variables));
                    if (executionContext.Variables.TryGetValue("system.culture", out culture) &&
                        !string.IsNullOrEmpty(culture?.Value))
                    {
                        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(culture.Value);
                        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(culture.Value);
                    }

                    AssemblyLoadContext.Default.Resolving += ResolveAssembly;
                    IAgentTaskPlugin taskPlugin = null;
                    try
                    {
                        Type type = Type.GetType(assemblyQualifiedName, throwOnError: true);
                        taskPlugin = Activator.CreateInstance(type) as IAgentTaskPlugin;
                        ArgUtil.NotNull(taskPlugin, nameof(taskPlugin));
                        taskPlugin.RunAsync(executionContext, tokenSource.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException oce)
                    {
                        // Treat cancellation as a graceful completion
                        executionContext?.Debug($"Task plugin cancelled: {oce.Message}");
                    }
                    catch (SocketException ex)
                    {
                        string url = executionContext?.VssConnection?.Uri?.ToString() ?? string.Empty;
                        ExceptionsUtil.HandleSocketException(ex, url, executionContext.Error);
                    }
                    catch (AggregateException ex)
                    {
                        ExceptionsUtil.HandleAggregateException(ex, executionContext.Error);
                    }
                    catch (TypeLoadException tle)
                    {
                        executionContext.Error($"Unable to load task plugin type '{assemblyQualifiedName}': {tle.Message}");
                    }
                    catch (BadImageFormatException bife)
                    {
                        executionContext.Error($"Invalid assembly image for task plugin type '{assemblyQualifiedName}': {bife.Message}");
                    }
                    catch (FileLoadException fle)
                    {
                        executionContext.Error($"Failed to load plugin assembly: {fle.Message}");
                    }
                    catch (ReflectionTypeLoadException rtle)
                    {
                        executionContext.Error($"Failed to load types from plugin assembly: {rtle.Message}");
                    }
                    catch (Exception ex)
                    {
                        executionContext.Error(ex.Message);
                        executionContext.Debug(ex.StackTrace);
                    }
                    finally
                    {
                        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                        
                        // Ensure plugin resources are cleaned up
                        try
                        {
                            if (taskPlugin is IDisposable disposablePlugin)
                            {
                                disposablePlugin.Dispose();
                            }
                        }
                        catch (Exception disposeEx)
                        {
                            executionContext?.Debug($"Error disposing plugin: {disposeEx.Message}");
                        }
                    }

                    return 0;
                }
                else if (string.Equals("command", pluginType, StringComparison.OrdinalIgnoreCase))
                {
                    string assemblyQualifiedName = args[1];
                    ArgUtil.NotNullOrEmpty(assemblyQualifiedName, nameof(assemblyQualifiedName));

                    string serializedContext = Console.ReadLine();
                    ArgUtil.NotNullOrEmpty(serializedContext, nameof(serializedContext));

                    AgentCommandPluginExecutionContext executionContext = null;
                    try
                    {
                        executionContext = StringUtil.ConvertFromJson<AgentCommandPluginExecutionContext>(serializedContext);
                    }
                    catch (Exception jsonEx)
                    {
                        Console.Error.WriteLine($"Failed to parse command plugin context: {jsonEx.Message}");
                        return 1;
                    }
                    ArgUtil.NotNull(executionContext, nameof(executionContext));

                    AssemblyLoadContext.Default.Resolving += ResolveAssembly;
                    try
                    {
                        Type type = Type.GetType(assemblyQualifiedName, throwOnError: true);
                        var commandPlugin = Activator.CreateInstance(type) as IAgentCommandPlugin;
                        ArgUtil.NotNull(commandPlugin, nameof(commandPlugin));
                        commandPlugin.ProcessCommandAsync(executionContext, tokenSource.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException oce)
                    {
                        // Treat cancellation as a graceful completion
                        executionContext?.Debug($"Command plugin cancelled: {oce.Message}");
                    }
                    catch (SocketException sex)
                    {
                        string url = executionContext?.VssConnection?.Uri?.ToString() ?? string.Empty;
                        ExceptionsUtil.HandleSocketException(sex, url, executionContext.Error);
                    }
                    catch (AggregateException aex)
                    {
                        ExceptionsUtil.HandleAggregateException(aex, executionContext.Error);
                    }
                    catch (TypeLoadException tle)
                    {
                        executionContext.Error($"Unable to load command plugin type '{assemblyQualifiedName}': {tle.Message}");
                        executionContext.Debug(tle.ToString());
                    }
                    catch (BadImageFormatException bife)
                    {
                        executionContext.Error($"Invalid assembly image for command plugin type '{assemblyQualifiedName}': {bife.Message}");
                        executionContext.Debug(bife.ToString());
                    }
                    catch (Exception ex)
                    {
                        // any exception throw from plugin will fail the command.
                        executionContext.Error(ex.ToString());
                    }
                    finally
                    {
                        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                    }

                    return 0;
                }
                else if (string.Equals("log", pluginType, StringComparison.OrdinalIgnoreCase))
                {
                    // read commandline arg to get the instance id
                    var instanceId = args[1];
                    ArgUtil.NotNullOrEmpty(instanceId, nameof(instanceId));

                    // read STDIN, the first line will be the HostContext for the log plugin host
                    string serializedContext = Console.ReadLine();
                    ArgUtil.NotNullOrEmpty(serializedContext, nameof(serializedContext));
                    AgentLogPluginHostContext hostContext = StringUtil.ConvertFromJson<AgentLogPluginHostContext>(serializedContext);
                    ArgUtil.NotNull(hostContext, nameof(hostContext));

                    // create plugin object base on plugin assembly names from the HostContext
                    List<IAgentLogPlugin> logPlugins = new List<IAgentLogPlugin>();
                    AssemblyLoadContext.Default.Resolving += ResolveAssembly;
                    try
                    {
                        foreach (var pluginAssembly in hostContext.PluginAssemblies)
                        {
                            try
                            {
                                Type type = Type.GetType(pluginAssembly, throwOnError: true);
                                var logPlugin = Activator.CreateInstance(type) as IAgentLogPlugin;
                                ArgUtil.NotNull(logPlugin, nameof(logPlugin));
                                logPlugins.Add(logPlugin);
                            }
                            catch (Exception ex)
                            {
                                // any exception throw from plugin will get trace and ignore, error from plugins will not fail the job.
                                Console.WriteLine($"Unable to load plugin '{pluginAssembly}': {ex}");
                            }
                        }
                    }
                    finally
                    {
                        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                    }

                    // start the log plugin host
                    var logPluginHost = new AgentLogPluginHost(hostContext, logPlugins);
                    Task hostTask = logPluginHost.Run();
                    while (true)
                    {
                        var consoleInput = Console.ReadLine();
                        if (string.Equals(consoleInput, $"##vso[logplugin.finish]{instanceId}", StringComparison.OrdinalIgnoreCase))
                        {
                            // singal all plugins, the job has finished.
                            // plugin need to start their finalize process.
                            logPluginHost.Finish();
                            break;
                        }
                        else
                        {
                            // the format is TimelineRecordId(GUID):Output(String)
                            logPluginHost.EnqueueOutput(consoleInput);
                        }
                    }

                    // wait for the host to finish.
                    hostTask.GetAwaiter().GetResult();

                    return 0;
                }
                else
                {
                    throw new ArgumentOutOfRangeException(pluginType);
                }
            }
            catch (Exception ex)
            {
                // infrastructure failure.
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= Console_CancelKeyPress;
            }
        }

        private static Assembly ResolveAssembly(AssemblyLoadContext context, AssemblyName assembly)
        {
            string assemblyFilename = assembly.Name + ".dll";
            if (string.IsNullOrEmpty(executingAssemblyLocation))
            {
                executingAssemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            return context.LoadFromAssemblyPath(Path.Combine(executingAssemblyLocation, assemblyFilename));
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            tokenSource.Cancel();
        }
    }
}
