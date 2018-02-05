﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using gfoidl.DataCompression.Wrappers;

namespace gfoidl.DataCompression
{
    /// <summary>
    /// Swinging door compression.
    /// </summary>
    /// <remarks>
    /// See documentation for further information.
    /// </remarks>
    public class SwingingDoorCompression : Compression
    {
        /// <summary>
        /// (Absolut) Compression deviation applied to the y values to calculate the
        /// min and max slopes.
        /// </summary>
        /// <remarks>
        /// Cf. CompDev in documentation.
        /// </remarks>
        public double CompressionDeviation { get; }
        //---------------------------------------------------------------------
        private double _maxDeltaX;
        /// <summary>
        /// Length of x before for sure a value gets recorded.
        /// </summary>
        /// <remarks>
        /// Cf. ExMax in documentation.<br />
        /// When specified as <see cref="DateTime" /> the <see cref="DateTime.Ticks" />
        /// are used.
        /// <para>
        /// When value is <c>null</c>, no value -- except the first and last -- are
        /// guaranteed to be recorded.
        /// </para>
        /// </remarks>
        public double? MaxDeltaX => _maxDeltaX == double.MaxValue ? (double?)null : _maxDeltaX;
        //---------------------------------------------------------------------
        /// <summary>
        /// Length of x/time within no value gets recorded (after the last archived value)
        /// </summary>
        public double? MinDeltaX { get; }
        //---------------------------------------------------------------------
        /// <summary>
        /// Creates a new instance of swinging door compression.
        /// </summary>
        /// <param name="compressionDeviation">
        /// (Absolut) Compression deviation applied to the y values to calculate the
        /// min and max slopes. Cf. CompDev in documentation.
        /// </param>
        /// <param name="maxDeltaX">
        /// Length of x before for sure a value gets recoreded. See <see cref="MaxDeltaX" />.
        /// </param>
        /// <param name="minDeltaX">
        /// Length of x/time within no value gets recorded (after the last archived value).
        /// See <see cref="MinDeltaX" />.
        /// </param>
        public SwingingDoorCompression(double compressionDeviation, double? maxDeltaX = null, double? minDeltaX = null)
        {
            this.CompressionDeviation = compressionDeviation;
            _maxDeltaX                = maxDeltaX ?? double.MaxValue;
            this.MinDeltaX            = minDeltaX;
        }
        //---------------------------------------------------------------------
        /// <summary>
        /// Creates a new instance of swinging door compression.
        /// </summary>
        /// <param name="compressionDeviation">
        /// (Absolut) Compression deviation applied to the y values to calculate the
        /// min and max slopes. Cf. CompDev in documentation.
        /// </param>
        /// <param name="maxTime">Length of time before for sure a value gets recoreded</param>
        /// <param name="minTime">Length of time within no value gets recorded (after the last archived value)</param>
        public SwingingDoorCompression(double compressionDeviation, TimeSpan maxTime, TimeSpan? minTime)
            : this(compressionDeviation, maxTime.Ticks, minTime?.Ticks)
        { }
        //---------------------------------------------------------------------
        /// <summary>
        /// Implementation of the compression / filtering.
        /// </summary>
        /// <param name="data">Input data</param>
        /// <returns>The compressed / filtered data.</returns>
        protected override IEnumerable<DataPoint> ProcessCore(IEnumerable<DataPoint> data)
        {
            if (data is ArrayWrapper<DataPoint> arrayWrapper)
            {
                return arrayWrapper.Count == 0
                    ? DataPointIterator.Empty
                    : new IndexedIterator<ArrayWrapper<DataPoint>>(this, arrayWrapper);
            }

            if (data is ListWrapper<DataPoint> listWrapper)
            {
                return listWrapper.Count == 0
                    ? DataPointIterator.Empty
                    : new IndexedIterator<ListWrapper<DataPoint>>(this, listWrapper);
            }

            if (data is DataPoint[] array)
            {
                return array.Length == 0
                    ? DataPointIterator.Empty
                    : new IndexedIterator<ArrayWrapper<DataPoint>>(this, new ArrayWrapper<DataPoint>(array));
            }

            if (data is List<DataPoint> list)
            {
                return list.Count == 0
                    ? DataPointIterator.Empty
                    : new IndexedIterator<ListWrapper<DataPoint>>(this, new ListWrapper<DataPoint>(list));
            }

            if (data is IList<DataPoint> ilist)
            {
                return ilist.Count == 0
                    ? DataPointIterator.Empty
                    : new IndexedIterator<IList<DataPoint>>(this, ilist);
            }

            IEnumerator<DataPoint> enumerator = data.GetEnumerator();
            return enumerator.MoveNext()
                ? new EnumerableIterator(this, enumerator)
                : DataPointIterator.Empty;
        }
        //---------------------------------------------------------------------
        private abstract class SwingingDoorCompressionIterator : DataPointIterator
        {
            protected readonly SwingingDoorCompression _swingingDoorCompression;
            protected (double Max, double Min)         _slope;
            protected (bool Archive, bool MaxDelta)    _archive;
            //-----------------------------------------------------------------
            protected SwingingDoorCompressionIterator(SwingingDoorCompression swingingDoorCompression)
                => _swingingDoorCompression = swingingDoorCompression;
            //-----------------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            protected void IsPointToArchive(in DataPoint lastArchived, in DataPoint incoming)
            {
                //ref (bool Archive, bool MaxDelta) archive = ref _archive;

                if ((incoming.X - lastArchived.X) >= (_swingingDoorCompression._maxDeltaX))
                {
                    _archive.Archive  = true;
                    _archive.MaxDelta = true;
                }
                else
                {
                    // Better to compare via gradient (1 calculation) than comparing to allowed y-values (2 calcuations)
                    // Obviously, the result should be the same ;-)
                    double slopeToIncoming = lastArchived.Gradient(incoming);

                    _archive.Archive  = slopeToIncoming < _slope.Min || _slope.Max < slopeToIncoming;
                    _archive.MaxDelta = false;
                }
            }
            //-----------------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            protected void CloseTheDoor(in DataPoint lastArchived, in DataPoint incoming)
            {
                double upperSlope = lastArchived.Gradient(incoming, _swingingDoorCompression.CompressionDeviation);
                double lowerSlope = lastArchived.Gradient(incoming, -_swingingDoorCompression.CompressionDeviation);

                if (upperSlope < _slope.Max) _slope.Max = upperSlope;
                if (lowerSlope > _slope.Min) _slope.Min = lowerSlope;
            }
            //-----------------------------------------------------------------
            protected void OpenNewDoor()
            {
                _slope.Max = double.PositiveInfinity;
                _slope.Min = double.NegativeInfinity;
            }
        }
        //---------------------------------------------------------------------
        private sealed class EnumerableIterator : SwingingDoorCompressionIterator
        {
            private readonly IEnumerator<DataPoint> _enumerator;
            private DataPoint                       _snapShot;
            private DataPoint                       _lastArchived;
            private DataPoint                       _incoming;
            //-----------------------------------------------------------------
            public EnumerableIterator(SwingingDoorCompression swingingDoorCompression, IEnumerator<DataPoint> enumerator)
                : base(swingingDoorCompression)
                => _enumerator = enumerator;
            //-----------------------------------------------------------------
            public override DataPointIterator Clone() => new EnumerableIterator(_swingingDoorCompression, _enumerator);
            //-----------------------------------------------------------------
            public override bool MoveNext()
            {
                switch (_state)
                {
                    default:
                        this.Dispose();
                        return false;
                    case 0:
                        _snapShot     = _enumerator.Current;
                        _lastArchived = _snapShot;
                        _incoming     = _snapShot;
                        _current      = _snapShot;
                        this.OpenNewDoor();
                        _state        = 1;
                        return true;
                    case 1:
                        while (_enumerator.MoveNext())
                        {
                            _incoming       = _enumerator.Current;
                            this.IsPointToArchive(_lastArchived, _incoming);
                            ref var archive = ref _archive;

                            if (!archive.Archive)
                            {
                                this.CloseTheDoor(_lastArchived, _incoming);
                                _snapShot = _incoming;
                                continue;
                            }

                            if (!archive.MaxDelta)
                            {
                                _current = _snapShot;
                                _state   = 2;
                                return true;
                            }

                            goto case 2;
                        }

                        _state = -1;
                        if (_incoming != _lastArchived)
                        {
                            _current = _incoming;
                            return true;
                        }
                        return false;
                    case 2:
                        if (_swingingDoorCompression.MinDeltaX.HasValue)
                        {
                            double snapShot_x = _snapShot.X;
                            double minDeltaX  = _swingingDoorCompression.MinDeltaX.Value;

                            while (_enumerator.MoveNext())
                            {
                                _incoming = _enumerator.Current;
                                if ((_incoming.X - snapShot_x) > minDeltaX)
                                    break;
                            }
                        }

                        _current = _incoming;
                        _state   = 1;
                        this.OpenNewDoor(_incoming);
                        return true;
                    case DisposedState:
                        ThrowHelper.ThrowIfDisposed(nameof(DataPointIterator));
                        return false;
                }
            }
            //-----------------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void OpenNewDoor(in DataPoint incoming)
            {
                _lastArchived = incoming;
                _snapShot     = incoming;

                this.OpenNewDoor();
            }
        }
        //---------------------------------------------------------------------
        private sealed class IndexedIterator<TList> : SwingingDoorCompressionIterator where TList : IList<DataPoint>
        {
            private readonly TList _source;
            private int            _snapShotIndex;
            private int            _lastArchivedIndex;
            private int            _incomingIndex;
            //-----------------------------------------------------------------
            public IndexedIterator(SwingingDoorCompression swingingDoorCompression, TList source)
                : base(swingingDoorCompression)
                => _source = source;
            //-----------------------------------------------------------------
            public override DataPointIterator Clone() => new IndexedIterator<TList>(_swingingDoorCompression, _source);
            //-----------------------------------------------------------------
            public override bool MoveNext()
            {
                switch (_state)
                {
                    default:
                        this.Dispose();
                        return false;
                    case 0:
                        _snapShotIndex     = 0;
                        _lastArchivedIndex = 0;
                        _incomingIndex     = default;
                        _current           = _source[0];

                        if (_source.Count < 2)
                        {
                            _state = -1;
                            return true;
                        }

                        this.OpenNewDoor(0);
                        _state         = 1;
                        _incomingIndex = 1;
                        return true;
                    case 1:
                        TList source      = _source;
                        int snapShotIndex = _snapShotIndex;
                        int incomingIndex = _incomingIndex;

                        while (true)
                        {
                            // Actually a while loop, but so the range check can be eliminated
                            // https://github.com/dotnet/coreclr/issues/15476
                            if ((uint)incomingIndex >= (uint)source.Count || (uint)snapShotIndex >= (uint)source.Count)
                                break;

                            ref var archive = ref this.IsPointToArchive(incomingIndex);

                            if (!archive.Archive)
                            {
                                this.CloseTheDoor(incomingIndex);
                                snapShotIndex = incomingIndex++;
                                continue;
                            }

                            if (!archive.MaxDelta)
                            {
                                _current       = source[snapShotIndex];
                                _state         = 2;
                                _snapShotIndex = snapShotIndex;
                                _incomingIndex = incomingIndex;
                                return true;
                            }

                            _snapShotIndex = snapShotIndex;
                            _incomingIndex = incomingIndex;
                            goto case 2;
                        }

                        _state = -1;
                        incomingIndex--;
                        if (incomingIndex != _lastArchivedIndex)
                        {
                            _current = source[incomingIndex];
                            return true;
                        }
                        return false;
                    case 2:
                        source        = _source;
                        incomingIndex = _incomingIndex;

                        if (_swingingDoorCompression.MinDeltaX.HasValue)
                        {
                            double snapShot_x = source[_snapShotIndex].X;
                            double minDeltaX  = _swingingDoorCompression.MinDeltaX.Value;

                            for (; incomingIndex < source.Count; ++incomingIndex)
                            {
                                DataPoint incoming = source[incomingIndex];
                                if ((incoming.X - snapShot_x) > minDeltaX)
                                    break;
                            }
                        }

                        _current       = source[incomingIndex];
                        _state         = 1;
                        this.OpenNewDoor(incomingIndex);
                        _incomingIndex = incomingIndex + 1;
                        return true;
                }
            }
            //-----------------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ref (bool Archive, bool MaxDelta) IsPointToArchive(int incomingIndex)
            {
                TList source          = _source;
                int lastArchivedIndex = _lastArchivedIndex;

                if ((uint)incomingIndex >= (uint)source.Count || (uint)lastArchivedIndex >= (uint)source.Count)
                    ThrowHelper.ThrowArgumentOutOfRange("incomingIndex or lastArchived");

                DataPoint lastArchived = source[lastArchivedIndex];
                DataPoint incoming     = source[incomingIndex];

                this.IsPointToArchive(lastArchived, incoming);
                return ref _archive;
            }
            //-----------------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void CloseTheDoor(int incomingIndex)
            {
                TList source          = _source;
                int lastArchivedIndex = _lastArchivedIndex;

                if ((uint)incomingIndex >= (uint)source.Count || (uint)lastArchivedIndex >= (uint)source.Count)
                    ThrowHelper.ThrowArgumentOutOfRange("incomingIndex or lastArchived");

                DataPoint lastArchived = source[lastArchivedIndex];
                DataPoint incoming     = source[incomingIndex];
                this.CloseTheDoor(lastArchived, incoming);
            }
            //---------------------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void OpenNewDoor(int incomingIndex)
            {
                _lastArchivedIndex = incomingIndex;
                _snapShotIndex     = incomingIndex;

                this.OpenNewDoor();
            }
        }
    }
}
