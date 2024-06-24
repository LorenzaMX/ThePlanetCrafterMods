﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FeatMultiplayer.MessageTypes
{
    internal abstract class MessageInventoryChanged : MessageBase
    {
        internal int inventoryId;
        internal int itemId;
    }
}
