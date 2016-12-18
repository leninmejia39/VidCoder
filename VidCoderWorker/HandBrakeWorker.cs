﻿using System;
using System.Diagnostics;
using System.Linq;
using System.ServiceModel;
using HandBrake.ApplicationServices.Interop;
using HandBrake.ApplicationServices.Interop.Json.Encode;
using HandBrake.ApplicationServices.Interop.Json.Scan;
using Microsoft.Practices.ServiceLocation;
using Microsoft.Practices.Unity;
using Newtonsoft.Json;
using VidCoderCommon;
using VidCoderCommon.Model;
using VidCoderCommon.Services;

namespace VidCoderWorker
{
	public class HandBrakeWorker : IHandBrakeWorker
	{
		private IHandBrakeWorkerCallback callback;
		private HandBrakeInstance instance;
		private object encodeLock = new object();

		// These are saved when SetUpWorker is called and used when StartScan or StartEncode is called.
		private int passedVerbosity;
		private int passedPreviewCount;
		private double passedMinTitleDurationSeconds;

		// True if we are encoding (not scanning)
		private EncodeState state = EncodeState.NotStarted;

		public void SetUpWorker(
			int verbosity, 
			int previewCount, 
			bool useDvdNav,
			double minTitleDurationSeconds,
			double cpuThrottlingFraction,
			string tempFolder)
		{
			this.passedVerbosity = verbosity;
			this.passedPreviewCount = previewCount;
			this.passedMinTitleDurationSeconds = minTitleDurationSeconds;

			CurrentWorker = this;
			this.callback = OperationContext.Current.GetCallbackChannel<IHandBrakeWorkerCallback>();

			Ioc.Container.RegisterInstance<ILogger>(new WorkerLogger(this.callback));

			try
			{
				if (!string.IsNullOrEmpty(tempFolder))
				{
					Environment.SetEnvironmentVariable("TMP", tempFolder, EnvironmentVariableTarget.Process);
				}

				if (cpuThrottlingFraction < 1.0)
				{
					int coresToUse = (int)Math.Round(Environment.ProcessorCount * cpuThrottlingFraction);
					if (coresToUse < 1)
					{
						coresToUse = 1;
					}

					if (coresToUse > Environment.ProcessorCount)
					{
						coresToUse = Environment.ProcessorCount;
					}

					Process process = Process.GetCurrentProcess();
					long affinityMask = 0x0;
					for (int i = 0; i < coresToUse; i++)
					{
						affinityMask |= (uint)(1 << i);
					}

					process.ProcessorAffinity = (IntPtr)affinityMask;
				}

				if (this.callback == null)
				{
					throw new ArgumentException("Could not get callback channel.");
				}


				HandBrakeUtils.MessageLogged += (o, e) =>
				{
					this.StopOnException(() =>
					{
						this.callback.OnMessageLogged(e.Message);
					});
				};

				HandBrakeUtils.ErrorLogged += (o, e) =>
				{
					this.StopOnException(() =>
					{
						this.callback.OnErrorLogged(e.Message);
					});
				};

				HandBrakeUtils.SetDvdNav(useDvdNav);
			}
			catch (Exception exception)
			{
				this.callback.OnException(exception.ToString());
				throw;
			}
		}

		public void StartEncode(
			VCJob job,
			int previewNumber,
			int previewSeconds,
			bool dxvaDecoding,
			string defaultChapterNameFormat)
		{
#if DEBUG_REMOTE
			Debugger.Launch();
#endif

			try
			{
				this.instance = new HandBrakeInstance();
				this.instance.Initialize(this.passedVerbosity);

				this.instance.ScanCompleted += (o, e) =>
				{
					try
					{
						SourceTitle encodeTitle = this.instance.Titles.TitleList.FirstOrDefault(title => title.Index == job.Title);
						if (encodeTitle != null)
						{
							JsonEncodeFactory factory = new JsonEncodeFactory(ServiceLocator.Current.GetInstance<ILogger>());

							JsonEncodeObject encodeObject = factory.CreateJsonObject(
								job,
								encodeTitle,
								defaultChapterNameFormat,
								dxvaDecoding,
								previewNumber,
								previewSeconds,
								this.passedPreviewCount);

							////this.callback.OnVidCoderMessageLogged("Encode JSON:" + Environment.NewLine + JsonConvert.SerializeObject(encodeObject, Formatting.Indented));

							lock (this.encodeLock)
							{
								this.instance.StartEncode(encodeObject);
								this.callback.OnEncodeStarted();
								this.state = EncodeState.Encoding;
							}
						}
						else
						{
							this.callback.OnEncodeComplete(error: true);
							this.CleanUpAndSignalCompletion();
						}
					}
					catch (Exception exception)
					{
						this.callback.OnException(exception.ToString());
						this.CleanUpAndSignalCompletion();
					}
				};

				this.instance.EncodeProgress += (o, e) =>
				{
					this.StopOnException(() =>
					{
						this.callback.OnEncodeProgress((float)e.AverageFrameRate, (float)e.CurrentFrameRate, e.EstimatedTimeLeft, (float)e.FractionComplete, e.PassId, e.Pass, e.PassCount);
					});
				};

				this.instance.EncodeCompleted += (o, e) =>
				{
					this.state = EncodeState.Finished;

					try
					{
						this.callback.OnEncodeComplete(e.Error);
					}
					catch (CommunicationException exception)
					{
						WorkerErrorLogger.LogError("Got exception when reporting completion: " + exception, isError: true);
					}
					finally
					{
						this.CleanUpAndSignalCompletion();
					}
				};


				this.instance.StartScan(job.SourcePath, this.passedPreviewCount, TimeSpan.FromSeconds(this.passedMinTitleDurationSeconds), job.Title);
				this.state = EncodeState.Scanning;
			}
			catch (Exception exception)
			{
				this.callback.OnException(exception.ToString());
				throw;
			}
		}

		public static HandBrakeWorker CurrentWorker { get; private set; }

		public void PauseEncode()
		{
			lock (this.encodeLock)
			{
				if (this.state == EncodeState.Encoding)
				{
					this.instance.PauseEncode();
				}
			}
		}

		public void ResumeEncode()
		{
			lock (this.encodeLock)
			{
				if (this.state == EncodeState.Encoding)
				{
					this.instance.ResumeEncode();
				}
			}
		}

		public void StopEncode()
		{
			this.StopEncodeIfPossible();
		}

		// Returns true if we sent a stop signal to the encode.
		public bool StopEncodeIfPossible()
		{
			lock (this.encodeLock)
			{
				if (this.state == EncodeState.Encoding)
				{
					this.instance.StopEncode();
					this.state = EncodeState.Stopping;
					return true;
				}
			}

			return false;
		}

		public string Ping()
		{
			return "OK";
		}

		public void CleanUp()
		{
			if (this.instance != null)
			{
				this.instance.Dispose();
			}

			HandBrakeUtils.DisposeGlobal();
		}

		private void CleanUpAndSignalCompletion()
		{
			this.CleanUp();
			Program.SignalEncodeComplete();
		}

		// Executes a callback operation and stops the encode when a communication exception occurs.
		private void StopOnException(Action action)
		{
			try
			{
				action();
			}
			catch (CommunicationException exception)
			{
				WorkerErrorLogger.LogError("Got exception: " + exception, isError: true);

				this.StopEncodeIfPossible();
			}
		}
	}
}