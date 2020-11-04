﻿using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Speckle.Core.Models;
using Speckle.Core.Transports;
using System.Collections.Concurrent;
using Speckle.Core.Logging;
using Sentry.Protocol;
using System.Threading;
using System.Collections.Generic;

namespace Speckle.Core.Api
{
  public static partial class Operations
  {

    /// <summary>
    /// Receives an object from a transport.
    /// </summary>
    /// <param name="objectId"></param>
    /// <param name="remoteTransport">The transport to receive from.</param>
    /// <param name="localTransport">Leave null to use the default cache.</param>
    /// <param name="onProgressAction">Action invoked on progress iterations.</param>
    /// <param name="onErrorAction">Action invoked on internal errors.</param>
    /// <param name="onTotalChildrenCountKnown">Action invoked once the total count of objects is known.</param>
    /// <returns></returns>
    public static Task<Base> Receive(string objectId, ITransport remoteTransport = null, ITransport localTransport = null, Action<ConcurrentDictionary<string, int>> onProgressAction = null, Action<string, Exception> onErrorAction = null, Action<int> onTotalChildrenCountKnown = null)
    {
      return Receive(
        objectId,
        CancellationToken.None,
        remoteTransport,
        localTransport,
        onProgressAction,
        onErrorAction,
        onTotalChildrenCountKnown
        );
    }

    /// <summary>
    /// Receives an object from a transport.
    /// </summary>
    /// <param name="objectId"></param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to send notice of cancellation.</param>
    /// <param name="remoteTransport">The transport to receive from.</param>
    /// <param name="localTransport">Leave null to use the default cache.</param>
    /// <param name="onProgressAction">Action invoked on progress iterations.</param>
    /// <param name="onErrorAction">Action invoked on internal errors.</param>
    /// <param name="onTotalChildrenCountKnown">Action invoked once the total count of objects is known.</param>
    /// <returns></returns>
    public static async Task<Base> Receive(string objectId, CancellationToken cancellationToken, ITransport remoteTransport = null, ITransport localTransport = null, Action<ConcurrentDictionary<string, int>> onProgressAction = null, Action<string, Exception> onErrorAction = null, Action<int> onTotalChildrenCountKnown = null)
    {
      Log.AddBreadcrumb("Receive");

      var (serializer, settings) = GetSerializerInstance();

      var localProgressDict = new ConcurrentDictionary<string, int>();
      var internalProgressAction = GetInternalProgressAction(localProgressDict, onProgressAction);

      localTransport = localTransport != null ? localTransport : new SQLiteTransport();
      localTransport.OnErrorAction = onErrorAction;
      localTransport.OnProgressAction = internalProgressAction;
      localTransport.CancellationToken = cancellationToken;

      serializer.ReadTransport = localTransport;
      serializer.OnProgressAction = internalProgressAction;
      serializer.OnErrorAction = onErrorAction;
      serializer.CancellationToken = cancellationToken;

      // First we try and get the object from the local transport. If it's there, we assume all its children are there, and proceed with deserialisation. 
      // This assumption is hard-wired into the SDK. Read below. 
      var objString = localTransport.GetObject(objectId);

      if (objString != null)
      {
        // Shoot out the total children count
        var partial = JsonConvert.DeserializeObject<Placeholder>(objString);
        if (partial.__closure != null)
          onTotalChildrenCountKnown?.Invoke(partial.__closure.Count);

        return JsonConvert.DeserializeObject<Base>(objString, settings);
      }
      else if (remoteTransport == null)
      {
        Log.CaptureAndThrow(new SpeckleException($"Could not find specified object using the local transport, and you didn't provide a fallback remote from which to pull it."), SentryLevel.Error);
      }

      // If we've reached this stage, it means that we didn't get a local transport hit on our object, so we will proceed to get it from the provided remote transport. 
      // This is done by copying itself and all its children from the remote transport into the local one.
      remoteTransport.OnErrorAction = onErrorAction;
      remoteTransport.OnProgressAction = internalProgressAction;
      remoteTransport.CancellationToken = cancellationToken;

      Log.AddBreadcrumb("RemoteHit");
      objString = await remoteTransport.CopyObjectAndChildren(objectId, localTransport, onTotalChildrenCountKnown);
      
      // Wait for the local transport to finish "writing" - in this case, it signifies that the remote transport has done pushing copying objects into it. (TODO: I can see some scenarios where latency can screw things up, and we should rather wait on the remote transport).
      await localTransport.WriteComplete();

      // Proceed to deserialise the object, now safely knowing that all its children are present in the local (fast) transport. 
      return JsonConvert.DeserializeObject<Base>(objString, settings);

      // Summary: 
      // Basically, receiving an object (and all its subchildren) operates with two transports, one that is potentially slow, and one that is fast. 
      // The fast transport ("localTransport") is used syncronously inside the deserialisation routine to get the value of nested references and set them. The slow transport ("remoteTransport") is used to get the raw data and populate the local transport with all necessary data for a successful deserialisation of the object. 
      // Note: if properly implemented, there is no hard distinction between what is a local or remote transport; it's still just a transport. So, for example, if you want to receive an object without actually writing it first to a local transport, you can just pass a Server/S3 transport as a local transport. 
      // This is not reccommended, but shows what you can do. Another tidbit: the local transport does not need to be disk-bound; it can easily be an in memory transport. In memory transports are the fastest ones, but they're of limited use for more 
    }

    internal class Placeholder
    {
      public Dictionary<string, int> __closure { get; set; } = new Dictionary<string, int>();
    }

  }
}