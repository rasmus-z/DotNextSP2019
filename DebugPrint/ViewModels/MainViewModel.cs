﻿using Microsoft.O365.Security.ETW;
using Microsoft.O365.Security.ETW.Kernel;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;

namespace DebugPrint.ViewModels {
	sealed class MainViewModel : BindableBase, IDisposable {
		public ObservableCollection<DebugItem> DebugItems { get; } = new ObservableCollection<DebugItem>();

		readonly KernelTrace _trace;
		readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
		readonly EventWaitHandle _dataReadyEvent, _bufferReadyEvent;
		readonly MemoryMappedFile _mmf;
		readonly MemoryMappedViewStream _stm;
		const int _bufferSize = 1 << 12;

		public MainViewModel() {
			_trace = new KernelTrace("DebugPrintTrace");
			var provider = new DebugPrintProvider();
			provider.OnEvent += OnEvent;
			_trace.Enable(provider);

			_bufferReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_BUFFER_READY");
			_dataReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_DATA_READY");
			_stopEvent = new AutoResetEvent(false);

			_mmf = MemoryMappedFile.CreateOrOpen("DBWIN_BUFFER", _bufferSize);
			_stm = _mmf.CreateViewStream();
		}

		private void OnEvent(IEventRecord record) {
			var item = new DebugItem {
				Time = record.Timestamp,
				ProcessId = (int)record.ProcessId,
				ProcessName = TryGetProcessName(record.ProcessId),
				ThreadId = (int)record.ThreadId,
				Text = record.GetAnsiString("Message").TrimEnd('\n', '\r'),
				Component = record.GetUInt32("Component", 0),
				IsKernel = true
			};
			_dispatcher.InvokeAsync(() => DebugItems.Add(item));
		}

		private string TryGetProcessName(uint processId) {
			try {
				return Process.GetProcessById((int)processId)?.ProcessName;
			}
			catch {
				return string.Empty;
			}
		}

		public void Dispose() {
			_trace.Dispose();
			_stopEvent?.Dispose();
			_bufferReadyEvent?.Dispose();
			_dataReadyEvent?.Dispose();
			_stm?.Dispose();
			_mmf?.Dispose();
		}

		string _searchText;
		public string SearchText {
			get => _searchText;
			set {
				if (SetProperty(ref _searchText, value)) {
					var view = CollectionViewSource.GetDefaultView(DebugItems);
					if (value == null)
						view.Filter = null;
					else {
						var text = value.ToLower();
						view.Filter = obj => {
							var item = (DebugItem)obj;
							return item.ProcessName.ToLower().Contains(text) || item.Text.ToLower().Contains(text);
						};
					}
				}
			}
		}

		public DelegateCommand ClearAllCommand => new DelegateCommand(() => DebugItems.Clear());

		bool _isRunningKernel, _isRunningUser;
		public bool IsRunningKernel {
			get => _isRunningKernel;
			set {
				if (SetProperty(ref _isRunningKernel, value)) {
					if (value) {
						var t = new Thread(() => _trace.Start()) {
							IsBackground = true
						};
						t.Start();
					}
					else {
						_trace.Stop();
					}
				}
			}
		}

		readonly AutoResetEvent _stopEvent;
		public bool IsRunningUser {
			get => _isRunningUser;
			set {
				if (SetProperty(ref _isRunningUser, value)) {
					if (value) {
						var t = new Thread(() => {
							var reader = new BinaryReader(_stm);
							var bytes = new byte[_bufferSize];
							do {
								_bufferReadyEvent.Set();
								if (_dataReadyEvent.WaitOne(400)) {
									var time = DateTime.Now;
									_stm.Position = 0;
									var pid = reader.ReadInt32();
									_stm.Read(bytes, 0, _bufferSize - sizeof(int));
									int index = Array.IndexOf<byte>(bytes, (byte)0);
									var text = Encoding.ASCII.GetString(bytes, 0, index - 1).TrimEnd('\n', '\r');
									var item = new DebugItem {
										ProcessId = pid,
										Text = text,
										Time = time,
										ProcessName = TryGetProcessName((uint)pid)
									};
									_dispatcher.InvokeAsync(() => DebugItems.Add(item));
								}
							} while (!_stopEvent.WaitOne(0));
						});
						t.IsBackground = true;
						t.Start();
					}
					else {
						_stopEvent.Set();
					}
				}
			}
		}
	}
}
