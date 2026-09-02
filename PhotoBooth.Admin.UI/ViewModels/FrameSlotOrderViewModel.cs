using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class NumberedFrameSlot : ObservableObject
    {
        int? orderNumber;

        public NumberedFrameSlot(FrameSlot slot, int? number = null)
        {
            Slot = slot;
            orderNumber = number;
        }

        public FrameSlot Slot { get; }
        public Guid Id => Slot.Id;
        public int X => Slot.X;
        public int Y => Slot.Y;
        public int Width => Slot.Width;
        public int Height => Slot.Height;
        public int? OrderNumber
        {
            get => orderNumber;
            set
            {
                if (!Set(ref orderNumber, value)) return;
                Raise(nameof(NumberText));
                Raise(nameof(IsNumbered));
            }
        }
        public string NumberText => OrderNumber.HasValue ? OrderNumber.Value.ToString() : "+";
        public bool IsNumbered => OrderNumber.HasValue;
    }

    public sealed class FrameSlotOrderViewModel : PageViewModel
    {
        readonly IFrameService frames;
        readonly INavigationService navigation;
        readonly ILogger<FrameSlotOrderViewModel> log;
        Frame frame;
        string message;
        Action<Guid, bool> closed;

        public FrameSlotOrderViewModel(IFrameService frameService, INavigationService navigationService,
            ILogger<FrameSlotOrderViewModel> logger)
        {
            frames = frameService;
            navigation = navigationService;
            log = logger;
            ToggleSlotCommand = new RelayCommand(ToggleSlot);
            ResetCommand = new RelayCommand(_ => Reset());
            SaveCommand = new AsyncCommand(_ => Save(), _ => CanSave);
            CancelCommand = new RelayCommand(_ => Close(false));
        }

        public override string Title => "Sắp xếp thứ tự ô ảnh";
        public ObservableCollection<NumberedFrameSlot> Slots { get; } = new ObservableCollection<NumberedFrameSlot>();
        public Frame Frame { get => frame; private set => Set(ref frame, value); }
        public string Message { get => message; private set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public int NumberedCount => Slots.Count(x => x.IsNumbered);
        public int TotalCount => Slots.Count;
        public bool CanSave => TotalCount > 0 && NumberedCount == TotalCount;
        public string ProgressText => "Đã đánh số " + NumberedCount + " / " + TotalCount + " ô ảnh";
        public string SaveHint => CanSave ? "Đã đủ thứ tự. Bạn có thể lưu thay đổi." : "Đánh số đủ tất cả ô ảnh để bật nút lưu.";
        public ICommand ToggleSlotCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        public void Open(Frame value, Action<Guid, bool> onClosed)
        {
            if (value == null) return;
            Frame = value;
            closed = onClosed;
            Slots.Clear();
            foreach (var slot in (value.Slots ?? new FrameSlot[0]).OrderBy(x => x.Index))
                Slots.Add(new NumberedFrameSlot(slot));
            Message = null;
            RaiseState();
        }

        void ToggleSlot(object parameter)
        {
            var target = parameter as NumberedFrameSlot;
            if (target == null) return;

            if (target.OrderNumber.HasValue)
            {
                var removed = target.OrderNumber.Value;
                target.OrderNumber = null;
                foreach (var item in Slots.Where(x => x.OrderNumber > removed))
                    item.OrderNumber--;
            }
            else
            {
                target.OrderNumber = NumberedCount + 1;
            }

            Message = null;
            RaiseState();
        }

        void Reset()
        {
            foreach (var item in Slots) item.OrderNumber = null;
            Message = null;
            RaiseState();
        }

        async Task Save()
        {
            if (!CanSave || Frame == null) return;
            try
            {
                var orderedIds = Slots.OrderBy(x => x.OrderNumber.Value).Select(x => x.Id).ToList();
                await frames.SetSlotOrderAsync(Frame.Id, orderedIds, CancellationToken.None);
                Close(true);
            }
            catch (Exception error)
            {
                log.LogError(error, "Không thể lưu thứ tự ô ảnh");
                Message = "Không thể lưu thứ tự ô ảnh: " + error.Message;
            }
        }

        void Close(bool saved)
        {
            var id = Frame?.Id ?? Guid.Empty;
            var callback = closed;
            closed = null;
            if (id != Guid.Empty) callback?.Invoke(id, saved);
            navigation.Navigate("frames");
        }

        void RaiseState()
        {
            Raise(nameof(NumberedCount));
            Raise(nameof(TotalCount));
            Raise(nameof(CanSave));
            Raise(nameof(ProgressText));
            Raise(nameof(SaveHint));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
