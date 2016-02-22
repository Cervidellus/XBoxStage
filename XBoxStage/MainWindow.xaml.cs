using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;

using SharpDX.XInput;

using Thorlabs.MotionControl.DeviceManagerCLI;
using Thorlabs.MotionControl.GenericMotorCLI;
using Thorlabs.MotionControl.GenericMotorCLI.ControlParameters;
using Thorlabs.MotionControl.GenericMotorCLI.AdvancedMotor;
using Thorlabs.MotionControl.GenericMotorCLI.Settings;
using Thorlabs.MotionControl.IntegratedStepperMotorsCLI;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Windows.Media;

namespace XBoxStage
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        Task m_backgroundWorkTask;
        private Settings m_Settings;

        // Stage
        private string m_xAxisID = "45000001";
        private string m_yAxisID = "45000002";
        //private int m_typeID = 45; // Long travel stage
        private LongTravelStage m_deviceX = null;
        private LongTravelStage m_deviceY = null;
        private int m_waitLong = 60000;   // mS

        // XBox
        private SharpDX.XInput.Controller m_xbox = null;
        private SharpDX.XInput.State m_xboxState;
        private SharpDX.XInput.State m_xboxStateLast;

        public event EventHandler OnXBoxGamepadButtonPressA;
        public event EventHandler OnXBoxGamepadButtonPressAOneShot;
        public event EventHandler OnXBoxGamepadButtonPressB;
        public event EventHandler OnXBoxGamepadButtonPressBOneShot;

        // ----------------------------------------------------------------------
        // Properties which support binding in the UI
        // ----------------------------------------------------------------------

        private double _posX;
        public double PosX
        {
            get
            {
                return _posX;
            }
            set
            {
                _posX = value;
                NotifyPropertyChanged();
            }
        }
        private double _posY;
        public double PosY
        {
            get
            {
                return _posY;
            }
            set
            {
                _posY = value;
                NotifyPropertyChanged();
            }
        }

        private bool _stageInitialized = false;
        public bool StageInitialized        {
            get
            {
                return _stageInitialized;
            }
            set
            {
                _stageInitialized = value;
                EnabledBrush = new SolidColorBrush(_stageInitialized ? Colors.LightBlue : Colors.LightGray);
                NotifyPropertyChanged();
                NotifyPropertyChanged("EnabledBrush");
            }
        }

        public SolidColorBrush EnabledBrush
        {
            get; set;
        }


        // ----------------------------------------------------------------------
        // MainWindow
        // ----------------------------------------------------------------------
        public MainWindow() 
        {
            InitializeComponent();
            DataContext = this;

            m_Settings = Settings.Restore();

            m_backgroundWorkTask = Task.Run(() => PollGamepad());
            Closing += (s,e) => {
                Settings.Save(m_Settings);
            };

            OnXBoxGamepadButtonPressA += (s, e) =>
            {
                Debug.WriteLine("ButtonA");
            };

            OnXBoxGamepadButtonPressAOneShot += (s, e) =>
            {
                Debug.WriteLine("ButtonAOneShot");
            };

            OnXBoxGamepadButtonPressA += (s, e) =>
            {
                Debug.WriteLine("ButtonB");
            };

            OnXBoxGamepadButtonPressBOneShot += (s, e) =>
            {
                Debug.WriteLine("ButtonBOneShot");
            };

            StageInitialized = false;
        }

        // ----------------------------------------------------------------------
        // Property notification boilerplate
        // ----------------------------------------------------------------------

        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        // ----------------------------------------------------------------------
        // XBox
        // ----------------------------------------------------------------------

        private void FindXBoxController()
        {
            m_xbox  = new SharpDX.XInput.Controller(UserIndex.Any);
            if (!m_xbox.IsConnected)
            {
                Debug.WriteLine("Could not connect to XBox");
            }
        }

        // return true if the button state just transitioned from 0 to 1
        private bool ButtonOneShot (GamepadButtonFlags button)
        {
            return !m_xboxStateLast.Gamepad.Buttons.HasFlag(button) && m_xboxState.Gamepad.Buttons.HasFlag(button);
        }

        // return true if the button is pushed
        private bool ButtonPushed(GamepadButtonFlags button)
        {
            return m_xboxState.Gamepad.Buttons.HasFlag(button);
        }

        // Main XBox processing
        private void PollGamepad()
        {
            if ((m_xbox == null) || !m_xbox.IsConnected) return;

            m_xboxStateLast = m_xboxState;
            m_xboxState = m_xbox.GetState();

            if (ButtonPushed(GamepadButtonFlags.A)) OnXBoxGamepadButtonPressA.Invoke(this, null);
            if (ButtonOneShot(GamepadButtonFlags.A)) OnXBoxGamepadButtonPressAOneShot.Invoke(this, null);

            if (ButtonPushed(GamepadButtonFlags.B)) OnXBoxGamepadButtonPressB.Invoke(this, null);
            if (ButtonOneShot(GamepadButtonFlags.B)) OnXBoxGamepadButtonPressBOneShot.Invoke(this, null);

            var LX = m_xboxState.Gamepad.LeftThumbX;
            var LY = m_xboxState.Gamepad.LeftThumbY;

            //determine how far the controller is pushed
            var magnitude = Math.Sqrt(LX * LX + LY * LY);

            //determine the direction the controller is pushed
            var normalizedLX = LX / magnitude;
            var normalizedLY = LY / magnitude;

            var normalizedMagnitude = 0.0;

            //check if the controller is outside a circular dead zone
            if (magnitude > Gamepad.LeftThumbDeadZone)
            {
                //clip the magnitude at its expected maximum value
                if (magnitude > 32767) magnitude = 32767;

                //adjust magnitude relative to the end of the dead zone
                magnitude -= Gamepad.LeftThumbDeadZone;

                //optionally normalize the magnitude with respect to its expected range
                //giving a magnitude value of 0.0 to 1.0
                normalizedMagnitude = magnitude / (32767 - Gamepad.LeftThumbDeadZone);
            }
            else //if the controller is in the deadzone zero out the magnitude
            {
                magnitude = 0.0;
                normalizedMagnitude = 0.0;

                // m_deviceX.Stop(6000);
            }

            m_deviceX.MoveContinuous(MotorDirection.Forward);

        }

        // ----------------------------------------------------------------------
        // Stage
        // ----------------------------------------------------------------------

        private void buttonConnect_Click(object sender, RoutedEventArgs e)
        {
            //PosX = 100.999;
            //return;

            if (m_deviceX != null) return;

            if (m_xbox != null) FindXBoxController();

            // Try to create synthetic stages (but Create will fail!)
            //var sx = DeviceManagerCLI.RegisterSimulation(xAxisID, typeID, "X axis");
            //var sy = DeviceManagerCLI.RegisterSimulation(yAxisID, typeID, "Y axis");

            DeviceManagerCLI.BuildDeviceList();
            List<string> serialNumbers = DeviceManagerCLI.GetDeviceList(LongTravelStage.DevicePrefix);

            // X axis -------------------------------------------------------
            m_deviceX = LongTravelStage.CreateLongTravelStage(m_xAxisID);
            m_deviceX.Connect(m_xAxisID);
            var diX = m_deviceX.GetDeviceInfo();

            // wait for the device settings to initialize
            if (!m_deviceX.IsSettingsInitialized())
            {
                m_deviceX.WaitForSettingsInitialized(5000);
            }

            // start the device polling            
            m_deviceX.StartPolling(250);

            // Y axis -------------------------------------------------------
            m_deviceY = LongTravelStage.CreateLongTravelStage(m_yAxisID);
            m_deviceY.Connect(m_xAxisID);
            var diY = m_deviceY.GetDeviceInfo();

            // wait for the device settings to initialize
            if (!m_deviceY.IsSettingsInitialized())
            {
                m_deviceY.WaitForSettingsInitialized(5000);
            }

            // start the device polling            
            m_deviceY.StartPolling(250);
            StageInitialized = true;
        }

        private void buttonDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (m_deviceX == null) return;

            m_deviceX.StopPolling();
            m_deviceX.Disconnect();
            m_deviceX = null;

            if (m_xbox != null)
            {
                m_xbox = null;
            }
            StageInitialized = false;
        }

        private void buttonHome_click(object sender, RoutedEventArgs e)
        {
            m_deviceX.Home(m_waitLong);
            m_deviceY.Home(m_waitLong);
        }

        private void buttonTest_click(object sender, RoutedEventArgs e)
        {
            // Generic tester function
            StageInitialized = !StageInitialized;
        }
    }
}
