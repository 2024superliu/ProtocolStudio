using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WpfProtocolStudio.Helpers
{
    /// <summary>
    /// WPF 命令绑定基础类 
    /// </summary>
    public class RelayCommand : ICommand
    {
        //按钮被点击时要执行的方法
        public readonly Action<object> _execute;
        //判断按钮当前能否被点击的方法
        public readonly Predicate<object> _canExecute;
        //自动更新按钮启用/禁用状态
        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
        //带参数的命令
        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        //无参数的命令
        public RelayCommand(Action execute) : this(_ => execute()) { }
        //判断按钮当前能否被点击
        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
        //点击按钮时调用的方法
        public void Execute(object parameter) => _execute(parameter);
    }
}
