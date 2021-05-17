﻿using System;
using UnityEngine;
using KSP.Localization;

namespace MuMech.AttitudeControllers
{
    class BetterController : BaseAttitudeController
    {
        private static readonly Vector3d _vector3dnan = new Vector3d(double.NaN, double.NaN, double.NaN);
        
        private                 Vessel   Vessel => ac.vessel;

        [Persistent(pass = (int) (Pass.Type | Pass.Global))]
        private readonly EditableDouble VelKp = new EditableDouble(18);
        [Persistent(pass = (int) (Pass.Type | Pass.Global))]
        private readonly EditableDouble VelKi = new EditableDouble(72);
        [Persistent(pass = (int) (Pass.Type | Pass.Global))]
        private readonly EditableDouble VelKd = new EditableDouble(1.125);
        [Persistent(pass = (int) (Pass.Type | Pass.Global))]
        private readonly EditableDouble VelN = new EditableDouble(20);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private readonly EditableDouble VelAlphaIn = new EditableDouble(0.1);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private readonly EditableDouble VelAlphaOut = new EditableDouble(1);

        [Persistent(pass = (int) (Pass.Type | Pass.Global))]
        private readonly EditableDouble PosAlphaIn = new EditableDouble(0.1);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private readonly EditableDouble PosKp = new EditableDouble(1);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private readonly EditableDouble PosKi = new EditableDouble(0.1);

        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private readonly EditableDouble maxStoppingTime = new EditableDouble(2.0);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private readonly EditableDouble minFlipTime = new EditableDouble(20);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private readonly EditableDouble rollControlRange = new EditableDouble(5);

        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private bool useControlRange = true;
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        private bool useFlipTime = true;
        [Persistent(pass = (int) (Pass.Type | Pass.Global))]
        private bool useStoppingTime = true;
        
        private readonly PIDLoop[] _pid =
        {
            new PIDLoop(),
            new PIDLoop(),
            new PIDLoop()
        };

        /* error in pitch, roll, yaw */
        private Vector3d _error0 = Vector3d.zero;
        private Vector3d _error1 = _vector3dnan;

        /* max angular acceleration */
        private Vector3d _maxAlpha = Vector3d.zero;

        /* max angular rotation */
        private Vector3d _maxOmega     = Vector3d.zero;
        private Vector3d _omega1       = _vector3dnan;
        private Vector3d _omega0       = _vector3dnan;
        private Vector3d _targetOmega  = Vector3d.zero;
        private Vector3d _targetTorque = Vector3d.zero;
        private Vector3d _alpha0       = _vector3dnan;
        private Vector3d _alpha1       = _vector3dnan;
        private Vector3d _nextAlpha    = _vector3dnan;
        private Vector3d _actuation    = Vector3d.zero;
        private double   _iTerm;

        /* error */
        private double _errorTotal;
        
        public BetterController(MechJebModuleAttitudeController controller) : base(controller)
        {
        }

        private const double EPS = 2.2204e-16;

        public override void DrivePre(FlightCtrlState s, out Vector3d act, out Vector3d deltaEuler)
        {
            UpdatePredictionPI();

            deltaEuler = -_error0 * Mathf.Rad2Deg;

            for(int i = 0; i < 3; i++) {
                if (Math.Abs(_actuation[i]) < EPS || double.IsNaN(_actuation[i]))
                    _actuation[i] = 0;
            }

            act = _actuation;
        }

        private void UpdateError()
        {
            Transform vesselTransform = Vessel.ReferenceTransform;

            // 1. The Euler(-90) here is because the unity transform puts "up" as the pointy end, which is wrong.  The rotation means that
            // "forward" becomes the pointy end, and "up" and "right" correctly define e.g. AoA/pitch and AoS/yaw.  This is just KSP being KSP.
            // 2. We then use the inverse ship rotation to transform the requested attitude into the ship frame (we do everything in the ship frame
            // first, and then negate the error to get the error in the target reference frame at the end).
            Quaternion deltaRotation = Quaternion.Inverse(vesselTransform.transform.rotation * Quaternion.Euler(-90, 0, 0))  * ac.RequestedAttitude;

            // get us some euler angles for the target transform
            Vector3d ea  = deltaRotation.eulerAngles;
            double pitch = ea[0] * UtilMath.Deg2Rad;
            double yaw   = ea[1] * UtilMath.Deg2Rad;
            double roll  = ea[2] * UtilMath.Deg2Rad;

            // law of cosines for the "distance" of the miss in radians
            _errorTotal = Math.Acos( MuUtils.Clamp( Math.Cos(pitch)*Math.Cos(yaw), -1, 1 ) );

            // this is the initial direction of the great circle route of the requested transform
            // (pitch is latitude, yaw is -longitude, and we are "navigating" from 0,0)
            // doing this calculation is the ship frame is a bit easier to reason about.
            Vector3d temp = new Vector3d(Math.Sin(pitch), Math.Cos(pitch) * Math.Sin(-yaw), 0);
            temp = temp.normalized * _errorTotal;

            // we assemble phi in the pitch, roll, yaw basis that vessel.MOI uses (right handed basis)
            Vector3d phi = new Vector3d(
                    MuUtils.ClampRadiansPi(temp[0]), // pitch distance around the geodesic
                    MuUtils.ClampRadiansPi(roll),
                    MuUtils.ClampRadiansPi(temp[1]) // yaw distance around the geodesic
                    );

            // apply the axis control from the parent controller
            phi.Scale(ac.AxisState);

            // the error in the ship's position is the negative of the reference position in the ship frame
            _error0 = -phi;
        }
        
        private void UpdatePredictionPI()
        {
            _omega0 = Vessel.angularVelocityD;

            if (_omega1.IsFinite())
                _alpha0 = (_omega0 - _omega1) / ac.vesselState.deltaT;
            else
                _alpha0 = 2 / ac.vesselState.deltaT * (_omega0 - _omega1) - _alpha1;

            UpdateError();
            
            // first tick after a reset we wait so we can measure angular accelleration
            if (!_alpha0.IsFinite())
                goto exit;

            // lowpass filter on the error input
            _error0 = _error1.IsFinite() ? _error1 + PosAlphaIn * (_error0 - _error1) : _error0;

            Vector3d controlTorque = ac.torque;

            // needed to stop wiggling at higher phys warp
            double warpFactor = ac.vesselState.deltaT / 0.02;

            // see https://archive.is/NqoUm and the "Alt Hold Controller", the acceleration PID is not implemented so we only
            // have the first two PIDs in the cascade.
            for (int i = 0; i < 3; i++)
            {
                double error = _error0[i];

                _maxAlpha[i] = controlTorque[i] / Vessel.MOI[i];

                double warpGain = PosKp / warpFactor;
                double effLD = _maxAlpha[i] / (2 * warpGain * warpGain);

                if (Math.Abs(error) <= 2 * effLD)
                {
                    if (_actuation.magnitude < 0.1)
                        _iTerm += error * 0.02;

                    // linear ramp down of acceleration
                    _targetOmega[i] = -PosKp * error - PosKi * _iTerm;
                }
                else
                {
                    // v = - sqrt(2 * F * x / m) is target stopping velocity based on distance
                    _iTerm          = 0;
                    _targetOmega[i] = -Math.Sqrt(2 * _maxAlpha[i] * (Math.Abs(error) - effLD)) * Math.Sign(error);
                }

                if (useStoppingTime)
                {
                    _maxOmega[i] = _maxAlpha[i] * maxStoppingTime;
                    if (useFlipTime) _maxOmega[i] = Math.Max(_maxOmega[i], Math.PI / minFlipTime);
                    _targetOmega[i] = MuUtils.Clamp(_targetOmega[i], -_maxOmega[i], _maxOmega[i]);
                }
            }

            if (useControlRange && _errorTotal * Mathf.Rad2Deg  > rollControlRange)
                _targetOmega[1] = 0;

            for (int i = 0; i < 3; i++)
            {
                double d = _maxAlpha[i] * warpFactor;

                _pid[i].Kp        = VelKp / d;
                _pid[i].Ki        = VelKi / d;
                _pid[i].Kd        = VelKd / d;
                _pid[i].N         = VelN;
                _pid[i].Ts        = ac.vesselState.deltaT;
                _pid[i].AlphaIn   = MuUtils.Clamp01(VelAlphaIn * warpFactor);
                _pid[i].AlphaOut  = MuUtils.Clamp01(VelAlphaOut * warpFactor);
                _pid[i].MinOutput = -1;
                _pid[i].MaxOutput = 1;

                // need the negative from the pid due to KSP
                _actuation[i] = -_pid[i].Update(_targetOmega[i], _omega0[i]);

                _nextAlpha[i] = _actuation[i] * _maxAlpha[i];

                if (Math.Abs(_actuation[i]) < EPS || double.IsNaN(_actuation[i]))
                    _actuation[i] = 0;

                _targetTorque[i] = _actuation[i] / ac.torque[i];
            }

            exit:

            _error1 = _error0;
            _omega1 = _omega0;
            _alpha1 = _alpha0;
        }

        public override void Reset()
        {
            _alpha0 = _alpha1 = _omega0 = _omega1 = _error0 = _error1 = _vector3dnan;
            _iTerm  = 0;
            foreach (PIDLoop pid in _pid)
                pid.Reset();
        }

        public override void GUI()
        {
            GUILayout.BeginHorizontal();
            useStoppingTime = GUILayout.Toggle(useStoppingTime, "Maximum Stopping Time", GUILayout.ExpandWidth(false));
            maxStoppingTime.text = GUILayout.TextField(maxStoppingTime.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            useFlipTime = GUILayout.Toggle(useFlipTime, "Minimum Flip Time", GUILayout.ExpandWidth(false));
            minFlipTime.text = GUILayout.TextField(minFlipTime.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();

            if(!useStoppingTime)
                useFlipTime = false;

            GUILayout.BeginHorizontal();
            useControlRange = GUILayout.Toggle(useControlRange, Localizer.Format("#MechJeb_HybridController_checkbox2"), GUILayout.ExpandWidth(false));//"RollControlRange"
            rollControlRange.text = GUILayout.TextField(rollControlRange.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Position Kp", GUILayout.ExpandWidth(false));
            PosKp.text = GUILayout.TextField(PosKp.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Position Ki", GUILayout.ExpandWidth(false));
            PosKi.text = GUILayout.TextField(PosKi.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Position AlphaIn", GUILayout.ExpandWidth(false));
            PosAlphaIn.text = GUILayout.TextField(PosAlphaIn.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Velocity Kp", GUILayout.ExpandWidth(false));
            VelKp.text = GUILayout.TextField(VelKp.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Velocity Ki", GUILayout.ExpandWidth(false));
            VelKi.text = GUILayout.TextField(VelKi.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Velocity Kd", GUILayout.ExpandWidth(false));
            VelKd.text = GUILayout.TextField(VelKd.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Velocity Kd N", GUILayout.ExpandWidth(false));
            VelN.text = GUILayout.TextField(VelN.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Velocity AlphaIn", GUILayout.ExpandWidth(false));
            VelAlphaIn.text = GUILayout.TextField(VelAlphaIn.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Velocity AlphaOut", GUILayout.ExpandWidth(false));
            VelAlphaOut.text = GUILayout.TextField(VelAlphaOut.text, GUILayout.ExpandWidth(true), GUILayout.Width(60));
            GUILayout.EndHorizontal();
            
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#MechJeb_HybridController_label2"), GUILayout.ExpandWidth(true));//"Actuation"
            GUILayout.Label(MuUtils.PrettyPrint(_actuation), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Error", GUILayout.ExpandWidth(true));
            GUILayout.Label(MuUtils.PrettyPrint(_error0), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Omega", GUILayout.ExpandWidth(true));
            GUILayout.Label(MuUtils.PrettyPrint(_omega0), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("MaxOmega", GUILayout.ExpandWidth(true));
            GUILayout.Label(MuUtils.PrettyPrint(_maxOmega), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("TargetOmega", GUILayout.ExpandWidth(true));
            GUILayout.Label(MuUtils.PrettyPrint(_targetOmega), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#MechJeb_HybridController_label4"), GUILayout.ExpandWidth(true));//"TargetTorque"
            GUILayout.Label(MuUtils.PrettyPrint(_targetTorque), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#MechJeb_HybridController_label5"), GUILayout.ExpandWidth(true));//"ControlTorque"
            GUILayout.Label(MuUtils.PrettyPrint(ac.torque), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("MaxAlpha", GUILayout.ExpandWidth(true));
            GUILayout.Label(MuUtils.PrettyPrint(_maxAlpha), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
        }
    }
}