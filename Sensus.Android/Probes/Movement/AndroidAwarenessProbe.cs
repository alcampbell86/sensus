﻿// Copyright 2014 The Rector & Visitors of the University of Virginia
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Gms.Awareness;
using Android.Gms.Awareness.Fence;
using Android.Gms.Common;
using Android.Gms.Common.Apis;
using Newtonsoft.Json;
using Sensus.Exceptions;
using Sensus.Probes;
using Syncfusion.SfChart.XForms;

namespace Sensus.Android.Probes.Movement
{
    /// <summary>
    /// Base class for Google Awareness API probes.
    /// </summary>
    public abstract class AndroidAwarenessProbe : ListeningProbe
    {
        protected enum FenceUpdateAction
        {
            Add,
            Remove
        }

        public const string AWARENESS_PENDING_INTENT_ACTION = "SENSUS_AWARENESS_UPDATE";

        private AndroidAwarenessProbeBroadcastReceiver _awarenessBroadcastReceiver;
        private GoogleApiClient _awarenessApiClient;
        private PendingIntent _fencePendingIntent;

        [JsonIgnore]
        protected AndroidAwarenessProbeBroadcastReceiver AwarenessBroadcastReceiver
        {
            get
            {
                return _awarenessBroadcastReceiver;
            }
        }

        [JsonIgnore]
        protected GoogleApiClient AwarenessApiClient
        {
            get
            {
                return _awarenessApiClient;
            }
        }

        [JsonIgnore]
        protected override bool DefaultKeepDeviceAwake
        {
            get
            {
                return false;
            }
        }

        [JsonIgnore]
        protected override string DeviceAsleepWarning
        {
            get
            {
                return null;
            }
        }

        [JsonIgnore]
        protected override string DeviceAwakeWarning
        {
            get
            {
                return "This setting should not be enabled. It does not affect iOS and will unnecessarily reduce battery life on Android.";
            }
        }

        public AndroidAwarenessProbe()
        {
            _awarenessBroadcastReceiver = new AndroidAwarenessProbeBroadcastReceiver();
            _fencePendingIntent = PendingIntent.GetBroadcast(Application.Context, 0, new Intent(AWARENESS_PENDING_INTENT_ACTION), 0);
        }

        protected override void Initialize()
        {
            base.Initialize();

            // check for availability of Google Play Services
            int googlePlayServicesAvailability = GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(Application.Context);

            if (googlePlayServicesAvailability != ConnectionResult.Success)
            {
                string message = "Google Play Services are not available on this device.";

                if (googlePlayServicesAvailability == ConnectionResult.ServiceVersionUpdateRequired)
                {
                    message += " Please update your phone's Google Play Services app using the App Store. Then restart your study.";
                }

                message += " Email the study organizers and tell them you received the following error code:  " + googlePlayServicesAvailability;

                // the problem we've encountered is potentially fixable, so do not throw a NotSupportedException, as doing this would
                // disable the probe and prevent any future restart attempts from succeeding.
                throw new Exception(message);
            }

            // connect awareness client
            _awarenessApiClient = new GoogleApiClient.Builder(Application.Context).AddApi(Awareness.Api)

                .AddConnectionCallbacks(

                    bundle =>
                    {
                        SensusServiceHelper.Get().Logger.Log("Connected to Google Awareness API.", LoggingLevel.Normal, GetType());
                    },

                    status =>
                    {
                        SensusServiceHelper.Get().Logger.Log("Connection to Google Awareness API suspended. Status:  " + status, LoggingLevel.Normal, GetType());
                    })

                .Build();

            _awarenessApiClient.BlockingConnect();
        }

        protected override void StartListening()
        {
            // register receiver for all awareness intent actions
            Application.Context.RegisterReceiver(_awarenessBroadcastReceiver, new IntentFilter(AWARENESS_PENDING_INTENT_ACTION));
        }

        protected override void StopListening()
        {
            // stop broadcast receiver
            Application.Context.UnregisterReceiver(_awarenessBroadcastReceiver);

            // disconnect client
            _awarenessApiClient.Disconnect();
            _awarenessApiClient = null;
        }

        protected void UpdateRequestBuilder(AwarenessFence fence, string fenceKey, FenceUpdateAction action, ref FenceUpdateRequestBuilder requestBuilder)
        {
            if (action == FenceUpdateAction.Add)
            {
                requestBuilder.AddFence(fenceKey, fence, _fencePendingIntent);
            }
            else if (action == FenceUpdateAction.Remove)
            {
                requestBuilder.RemoveFence(fenceKey);
            }
        }

        protected bool UpdateFences(IFenceUpdateRequest updateRequest)
        {
            ManualResetEvent updateWait = new ManualResetEvent(false);

            bool success = false;

            try
            {
                // update fences is asynchronous
                Awareness.FenceApi.UpdateFences(_awarenessApiClient, updateRequest).SetResultCallback<Statuses>(status =>
                {
                    try
                    {
                        if (status.IsSuccess)
                        {
                            SensusServiceHelper.Get().Logger.Log("Updated Google Awareness API fences.", LoggingLevel.Normal, GetType());
                            success = true;
                        }
                        else if (status.IsCanceled)
                        {
                            SensusServiceHelper.Get().Logger.Log("Google Awareness API fence update canceled.", LoggingLevel.Normal, GetType());
                        }
                        else if (status.IsInterrupted)
                        {
                            SensusServiceHelper.Get().Logger.Log("Google Awareness API fence update interrupted", LoggingLevel.Normal, GetType());
                        }
                        else
                        {
                            string message = "Unrecognized fence update status:  " + status;
                            SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, GetType());
                            SensusException.Report(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Exception while processing update status:  " + ex, LoggingLevel.Normal, GetType());
                    }
                    finally
                    {
                        // ensure that wait is always set
                        updateWait.Set();
                    }
                });
            }
            // catch any errors from calling UpdateFences
            catch (Exception ex)
            {
                // ensure that wait is always set
                SensusServiceHelper.Get().Logger.Log("Exception while updating fences:  " + ex, LoggingLevel.Normal, GetType());
                updateWait.Set();
            }

            // we've seen cases where the update blocks indefinitely (e.g., due to outdated google play services on the phone). impose
            // a timeout to avoid such blocks.
            if (!updateWait.WaitOne(TimeSpan.FromSeconds(60)))
            {
                SensusServiceHelper.Get().Logger.Log("Timed out while updating fences.", LoggingLevel.Normal, GetType());
            }

            return success;
        }

        protected override ChartDataPoint GetChartDataPointFromDatum(Datum datum)
        {
            throw new NotImplementedException();
        }

        protected override ChartAxis GetChartPrimaryAxis()
        {
            throw new NotImplementedException();
        }

        protected override RangeAxisBase GetChartSecondaryAxis()
        {
            throw new NotImplementedException();
        }

        protected override ChartSeries GetChartSeries()
        {
            throw new NotImplementedException();
        }
    }
}
