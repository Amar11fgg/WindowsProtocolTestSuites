// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using SkiaSharp;

namespace Microsoft.Protocols.TestSuites.Rdpei
{
    public static class RdpeiSUTControlData
    {
        public static readonly RdpeiSUTControlConfig OneTouchEventControlData = new RdpeiSUTControlConfig
            (
            new RdpeiSUTControlInstruction[]
            {
                new RdpeiSUTControlInstruction("Please touch the screen.",RdpeiUtility.Paint_Arial_35 , 0, 0)
            },
            540,
            100
            );

        public static readonly RdpeiSUTControlConfig ContinuousTouchEventControlData = new RdpeiSUTControlConfig
            (
            new RdpeiSUTControlInstruction[]
            {
                new RdpeiSUTControlInstruction("Please touch the screen several times to trigger", RdpeiUtility.Paint_Arial_35, 0, 0),
                new RdpeiSUTControlInstruction("touch events (at least touch 5 times).", RdpeiUtility.Paint_Arial_28, 0, 70)
            },
            800,
            150
            );

        public static RdpeiSUTControlConfig MultitouchEventControlData(ushort count)
        {
            return new RdpeiSUTControlConfig
                (
                new RdpeiSUTControlInstruction[]
                {
                    new RdpeiSUTControlInstruction("Please multitouch the screen with", RdpeiUtility.Paint_Arial_35, 0, 0),
                    new RdpeiSUTControlInstruction($"2 to {count} fingers.",RdpeiUtility.Paint_Arial_35, 0, 70)
                },
                720,
                150
                );
        }

        public static readonly RdpeiSUTControlConfig PositionSpecifiedTouchEventControlData = new RdpeiSUTControlConfig
            (
            new RdpeiSUTControlInstruction[]
            {
                new RdpeiSUTControlInstruction("Please touch the red circles.", RdpeiUtility.Paint_Arial_35, 0, 0)
            },
            600,
            100
            );

        public static readonly RdpeiSUTControlConfig DismissHoveringContactPduControlData = new RdpeiSUTControlConfig
            (
            new RdpeiSUTControlInstruction[]
            {
                new RdpeiSUTControlInstruction("Does your device support proximity? If yes,",RdpeiUtility.Paint_Verdana_24, 0, 0),
                new RdpeiSUTControlInstruction("trigger RDPINPUT_DISMISS_HOVERING_CONTACT_PDU.",RdpeiUtility.Paint_Verdana_20, 0, 70),
                new RdpeiSUTControlInstruction("If no, touch the 'Exit' at the bottom right.",RdpeiUtility.Paint_Verdana_24 , 0, 140)
            },
            800,
            200
            );

        public static readonly RdpeiSUTControlConfig UnexpectedPositionNotice = new RdpeiSUTControlConfig
            (
            new RdpeiSUTControlInstruction[]
            {
                new RdpeiSUTControlInstruction("Oops...Unexpected position.", new SKPaint{Typeface=SKTypeface.FromFamilyName("Arial"),TextSize= 35*96/72}, 0, 0)
            },
            650,
            100
            );
    }

    /// <summary>
    /// A single line of control instruction.
    /// </summary>
    public struct RdpeiSUTControlInstruction
    {
        /// <summary>
        /// The text of the instruction
        /// </summary>
        public string text;

        /// <summary>
        /// The font of the instruction
        /// </summary>
        public SKPaint paint;

        /// <summary>
        /// The left position of current line of instruction text relative to the entire instruction area. 
        /// </summary>
        public ushort left;

        /// <summary>
        /// The top position of current line of instruction text relative to the entire instruction area. 
        /// </summary>
        public ushort top;

        public RdpeiSUTControlInstruction(string text, SKPaint paint, ushort left, ushort top)
        {
            this.text = text;
            this.paint = paint;
            this.left = left;
            this.top = top;
        }
    }

    /// <summary>
    /// The configuration of SUT control instructions.
    /// </summary>
    public struct RdpeiSUTControlConfig
    {
        /// <summary>
        /// The configuration of the instruction area.
        /// </summary>
        public RdpeiSUTControlInstruction[] instructions;

        /// <summary>
        /// The width of the instruction area.
        /// </summary>
        public ushort width;

        /// <summary>
        /// The height of the instruction area.
        /// </summary>
        public ushort height;

        public RdpeiSUTControlConfig(RdpeiSUTControlInstruction[] instructions, ushort width, ushort height)
        {
            this.instructions = instructions;
            this.width = width;
            this.height = height;
        }
    }
}
