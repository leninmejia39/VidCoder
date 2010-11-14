﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HandBrake.Interop;

namespace VidCoder.Model
{
	public class PreviewImageJob
	{
		public HandBrakeInstance ScanInstance { get; set; }

		public int UpdateVersion { get; set; }

		public int PreviewNumber { get; set; }

		/// <summary>
		/// Gets or sets the object to lock on before accessing the file cache image.
		/// </summary>
		public object ImageFileSync { get; set; }

		public EncodeJob EncodeJob { get; set; }
	}
}
