﻿using System;
using System.Linq;
using NumCIL.Generic;
using System.Collections.Generic;
using NumCIL.Generic;
using MongoDB.Driver;
using System.Windows.Markup;
using NumCIL;
using System.Runtime.InteropServices;
using Utilities;
using NumCIL.Boolean;
using System.Collections;
using System.Text;
using MongoDB.Driver.Core.WireProtocol.Messages;
using MongoDB.Bson;
using Utilities;
using NumCIL;

namespace NArctic
{
	public class SeriesList : IEnumerable<Series>
	{
		protected List<Series> Series = new List<Series> ();

		public int Count { get{ return Series.Count; } }

		public IEnumerator<Series> GetEnumerator () { return Series.GetEnumerator (); }

		IEnumerator IEnumerable.GetEnumerator () { return Series.GetEnumerator (); }

		public DType DType = new DType(typeof(IDictionary<string,object>));

		public event Action<SeriesList, IEnumerable<Series>, IEnumerable<Series>> SeriesListChanged;

		public int Add(Series s, string name=null){
			if (name != null)
				s.Name = name;
			this.Series.Add (s);
            if(SeriesListChanged!=null)
			    SeriesListChanged(this, new Series[]{s}, new Series[0]);
			this.DType.Fields.Add (s.DType);
			return this.Series.Count - 1;
		}

		public Series this[int i] {
			get {
				return Series [i];
			}
		}

		public string ToString(object[] args)
		{
			return DType.sep.Joined (
				Series.Select((s, i)=>s.DType.ToString(args[i]))
			);
		}

		public override string ToString ()
		{
			return string.Join (DType.sep, this.Series.Select (x => "{0}".Args(x.Name)));
		}
	}

	public class RowsList : IEnumerable<object[]>
	{
		protected DataFrame df;
		public int Count;
        private int Head;    // Free index (write to it)
        private int Tail;    // First used index (read from it)

		public RowsList(DataFrame df) 
		{
			this.df = df;
            this.Head = 0;
            this.Tail = 0;
		}

        public DataFrame Slice(Range rng)
        {
            var rtn = new DataFrame();
            foreach (var col in df.Columns)
            {
                rtn.Columns.Add(col[rng]);
            }
            return rtn;
        }

        public int Enqueue(Action<DataFrame, int> fill=null)
        {
            int next = (Head + 1) % Count;
            if (next == Tail)
                return -1;
            int head = Head;
            Head = next;
            if (fill != null)
                fill(this.df, head);
            return head;
        }

        public int Dequeue()
        {
            if (Head == Tail)
                return -1;
            int tail = Tail;
            Tail = (Tail + 1) % Count;
            return tail;
        }

        public int QueueLength
        {
            get
            {
                int used = (Head - Tail);
                if (used < 0)
                    used = Head + Count - Tail;
                return used;
            }
        }

		public object[] this[int row] 
		{
			get{ return this.df.Columns.Select (col => col.At(row)).ToArray(); }
		}

		public IEnumerator<object[]> GetEnumerator ()
		{
			for (int i = 0; i < Count; i++)
				yield return this [i];
		}

		IEnumerator IEnumerable.GetEnumerator ()
		{
			return ((IEnumerable<object[]>)this).GetEnumerator ();
		}

		public override string ToString ()
		{
			return ToString (5, 5);
		}

		public void OnColumnsChanged(SeriesList series, IEnumerable<Series> removed, IEnumerable<Series> added)
		{
			Series[] rm = removed.ToArray ();
			if(rm.Length==0)
				foreach (var s in added) {
                    if (s.Count > Count)
                        Count = s.Count;
				}
			else {
				int count = 0;
				foreach (var s in series)
					count = Math.Max (count, s.Count);
				Count = count;
			}
		}

		public string ToString (int head, int tail)
		{
			var sb = new StringBuilder ();
			int row = 0;
			for(row=0;row<Math.Min(head,this.Count);row++)
				sb.AppendFormat("[{0}] {1}\n".Args(row, DType.sep.Joined(this.df.Columns.ToString(this[row]))));
			sb.Append ("...\n");
			for(row=Math.Max(row+1,this.Count-tail);row<this.Count;row++)
				sb.AppendFormat("[{0}] {1}\n".Args(row, DType.sep.Joined(this.df.Columns.ToString(this[row]))));
			return sb.ToString ();
		}
	}

	public class DataFrame : IEnumerable<Series>
	{
		public SeriesList Columns = new SeriesList ();
		public RowsList Rows;

        public DType DType {
			get { return Columns.DType; } 
		}

		public DataFrame()
		{
			Rows = new RowsList (this);
			Columns.SeriesListChanged += this.OnColumnsChanged;
		}

		public DataFrame(IEnumerable<Series> series)
			: this()
		{
			foreach (var x in series) {
				this.Columns.Add (x);
			}
		}

        public DataFrame(int count, params Type[] types) 
            : this()
        {
            foreach(var t in types)
            {
                if (t == typeof(double))
                    Columns.Add(new Series<double>(count));
                else if (t == typeof(long))
                    Columns.Add(new Series<long>(count));
                else throw new ArgumentException("Type {0} not supported".Args(t));
            }
        }

		public DataFrame Clone() 
		{
			var df = new DataFrame (this.Columns.Select(x=>x.Clone()));
			return df;
		}

		public void OnColumnsChanged(SeriesList series, IEnumerable<Series> removed, IEnumerable<Series> added)
		{
			Rows.OnColumnsChanged (series, removed, added);
		}

		public DataFrame this [Range range] {
			get {
                return Rows.Slice(range);
			}
		}

		public static DataFrame FromBuffer(byte[] buf, DType buftype, int iheight)
		{
			var df = new DataFrame();
			var bytesPerRow = buftype.FieldOffset (buftype.Fields.Count);
			if(buf.Length < bytesPerRow*iheight)
				throw new InvalidOperationException("buf length is {0} but {1} expected".Args(buf.Length, bytesPerRow*iheight));
			for (int i = 0; i < buftype.Fields.Count; i++) {
				var s = Series.FromBuffer (buf, buftype, iheight, i); 
				s.Name = buftype.Name ?? "[{0}]".Args (i);
				df.Columns.Add (s);
				df.Rows.Count = Math.Max (df.Rows.Count, s.Count);
			}
			return df;
		}

		public byte[] ToBuffer() 
		{
			byte[] buf = new byte[this.Rows.Count*this.DType.FieldOffset(this.DType.Fields.Count)];
			for (int i = 0; i < Columns.Count; i++) {
				Columns [i].ToBuffer(buf, this.DType, this.Rows.Count, i);
			}
			return buf;
		}

		public Series this[int index] 
		{
			get{ return this.Columns [index]; }
		}

		public int Add(Series series, string name=null)
		{
			return this.Columns.Add (series, name);
		}

		public override string ToString ()
		{
			var sb = new StringBuilder ();
			sb.Append (Columns.ToString ());
			sb.Append ("\n");
			sb.Append (Rows.ToString ());
			return sb.ToString ();
		}

		public IEnumerator<Series> GetEnumerator ()
		{
			return this.Columns.GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator ()
		{
			return this.Columns.GetEnumerator();
		}
	}
}

