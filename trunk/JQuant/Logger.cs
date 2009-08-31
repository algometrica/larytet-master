
using System;
using System.IO;
using System.Threading;
using System.Collections;
using System.Collections.Generic;

namespace JQuant
{
	/// <summary>
	/// base class for all system loggers
	/// </summary>
	public abstract class Logger :IDisposable, ILogger
	{
        public Logger(string name)
        {
            _name = name;

            // add myself to the list of created mailboxes
            Resources.Loggers.Add(this);
        }

		/// <summary>
		/// application will call this method for proper cleanup
		/// </summary>
        public virtual void Dispose()
        {
            // remove myself from the list of created loggers
            Resources.Loggers.Remove(this);
        }
		
		public abstract void AddEntry(object o);

        ~Logger()
        {
            Console.WriteLine("Mailbox " + GetName() + " destroyed");
        }
		
        public string GetName()
		{
			return _name;
		}
		
        public int GetCount()
		{
			return _count;
		}
        
        public LogType GetLogType()
        {
            return Type;
        }
		
		public bool TimeStamped()
		{
			return _timeStamped;
		}
		
		public System.DateTime GetLatest()
		{
			return _stampLatest;
		}
		
		public System.DateTime GetOldest()
		{
			return _stampOldest;
		}
		
        public LogType Type
        {
            get;
            set;
        }

		protected string _name;
		protected int _count;
		protected bool _timeStamped;
		protected System.DateTime _stampLatest; 
		protected System.DateTime _stampOldest; 
	}
	
	/// <summary>
	/// write log to file
	/// base class for all loggers working with output streams
	/// the class implments asynchronous (postponned) writing and will be
	/// used when there large amounts of data to write or when the data should be
	/// processed before writing and the thread generating the data is 
	/// sensitive to the performance
    ///    
    /// Use property Priority to set the task priority. Default priority is 
    /// ThreadPriority.Lowest
    /// 
	/// </summary>
	public abstract class AsyncLogger :Logger
	{
		public AsyncLogger(string name): base(name)
		{
			notStoped = false;
			writer = new Thread(this.Writer);
			writer.Priority = ThreadPriority.Lowest;
            incomingData = new Queue(100);
		}
			
		/// <summary>
		/// start write file
		/// </summary>
		public virtual bool Start()
		{
            notStoped = true;
            
			// start Writer (writing thread)
			writer.Start();

            return true;
		}

		/// <summary>
		/// application will call this method for clean up
		/// remove registration from the producer
		/// close the file, remove registration of the data sync from the producer
		/// </summary>
		public virtual void Stop()
		{
			notStoped = false;
            lock (this)
			{
				incomingData.Clear();
			}			
		}
		
        /// <summary>
        /// Application calls this method to add entry to the log
        /// This method is non-blocking - add entry to the queue for 
        /// further processing and get out
        /// </summary>
		public override void AddEntry(object data)
		{
            lock (this)  // protect access to FIFO
			{
				// push the data to the FIFO
				incomingData.Enqueue(data);
				Monitor.Pulse(this);
			}
		}

        public ThreadPriority Priority
        {
            set {
                writer.Priority = value;
            }
            
            get
            {
                return writer.Priority;
            }
        }


        
		/// <summary>
		/// low priority thread writing the data to the file
		/// </summary>
		protected void Writer()
		{
			bool dataSet = false;
			object data = null;
			
			while (notStoped)
			{
				lock (this)
				{
					if (incomingData.Count != 0)
					{
						data = incomingData.Dequeue();
						dataSet = true;
					}
					else 
					{
						// i want to check the notStoped flag from time to time
						// and make sure that incomingData is empty
						Monitor.Wait(this, 1*1000);
					}
				}
				if (dataSet)
				{
					WriteData(data);
				}
			}
		}
		
		protected abstract void WriteData(object data);
		
		protected bool notStoped;
		
		/// <summary>
		/// Notify() pushes the incoming data here
		/// Thread FileWriter() pulls objects from the list and writes them to the file
		/// Under normal conditions the list is going to be empty most of the time
		/// </summary>
		protected Queue incomingData;
		protected Thread writer;
	}

	/// <summary>
	/// this class will get the data from specified data producer and write the data to the 
	/// specified file.
	/// </summary>
	public class MarketDataLogger :AsyncLogger, ISink<FMRShell.MarketData>
	{
        /// <summary>
        /// Create the ASCII logger
        /// </summary>
        /// <param name="name">
        /// A <see cref="System.String"/>
        /// Debuh info - name of the logger
        /// </param>
        /// <param name="filename">
        /// A <see cref="System.String"/>
        /// File to read the data
        /// </param>
        /// <param name="append">
        /// A <see cref="System.Boolean"/>
        /// If "append" is true and file exists logger will append the data to the end of the file
        /// </param>
        /// <param name="producer">
        /// A <see cref="IProducer"/>
        /// Object which provides data to log
        /// </param>
		public MarketDataLogger(string name, string filename, bool append, 
		                             IProducer<FMRShell.MarketData> producer): base(name)
		{
			FileName = filename;
            _fileStream = default(FileStream);
            _streamWriter = default(StreamWriter);
            _producer = producer;
            _append = append;
			notStoped = false;
            marketDataToString = new FMRShell.K300MaofTypeToString("\t");
		}
			
		/// <summary>
		/// register notifier in the producer, start write file
        /// returns True if Ok
        /// application will check LastException if the method
        /// returns False
		/// </summary>
		public override bool Start()
		{
            bool result = false;
            
            // i want a loop here to break from  - i avoid multiple
            // returns this way
            do
            {
                // open file for writing
                try
                {
                    if (_append)
                    {
                        _fileStream = new FileStream(FileName, FileMode.Append, FileAccess.Write, FileShare.Read);
                        _streamWriter = new StreamWriter(_fileStream);
                    }
                    else
                    {
                        _fileStream = new FileStream(FileName, FileMode.Create, FileAccess.Write, FileShare.Read);
                        _streamWriter = new StreamWriter(_fileStream);
                    }
                }
                catch (IOException e)
                {
                    // store the exception
                    LastException = e;
                    if (_fileStream != default(FileStream))
                    {
                        _fileStream.Close();
                    }
                    // and get out
                    break;
                }

                // write legend at the top of the file
                try
                {
                    _streamWriter.WriteLine(marketDataToString.Legend);
                    _streamWriter.Flush();
                }
                catch (IOException e)
                {
                    // store the exception
                    LastException = e;
                    // close the file
                    _fileStream.Close();
                    Console.WriteLine(e.ToString());
                    // and get out
                    break;
                }
             

                // register myself in the data producer
                _producer.AddSink(this);
                
                base.Start();       

                result = true;
            }
            while (false);

            return result;
		}

		/// <summary>
		/// application will call this method for clean up
		/// remove registration from the producer
		/// close the file, remove registration of the data sync from the producer
		/// </summary>
		public override void Stop()
		{
            lock (this)
			{
                _producer.RemoveSink(this);
                base.Stop();
                if (_fileStream != default(FileStream))
                {
                    _fileStream.Close();
                }
			}			
		}

        public override void Dispose()
        {
            _fileStream.Close();
            base.Dispose();
        }
		
		/// <summary>
		/// FMRShell collector calls this method to notify about incoming events
		/// push the evet into FIFO and let low priority background thread to write 
		/// the data into the file
		/// </summary>
		public void Notify(int count, FMRShell.MarketData data)
		{
            // i have to clone the data first. Producer can use the same
            // reference again and again
            FMRShell.MarketData dataClone = (FMRShell.MarketData)(data.Clone());

            // add the object to the queue for further processing
			base.AddEntry(data);
		}

        /// <summary>
        /// write data to the file. this method is called from a separate
        /// thread
        /// </summary>
        /// <param name="data">
        /// A <see cref="System.Object"/>
        /// </param>
		protected override void WriteData(object data)
		{
			// I have to decide on format of the log - ASCII or binary 
            // should I write any system info like version the data/software ?
            // at this point only ASCII is supported, no system info
            // write all fields of K300MaofType (data.k3Maof) in one line
            // followed by EOL
            FMRShell.MarketData md = (FMRShell.MarketData)data;

            // convert the data to string - this is time consuming
            // operation
            marketDataToString.Init(md.k300MaofType);
            
            // write the string to the file
            try
            {
                _streamWriter.WriteLine(marketDataToString.Values);
                _streamWriter.Flush();
            }
            catch (ObjectDisposedException e)
            {
                // store the exception
                LastException = e;
                Console.WriteLine(e.ToString());
            }
            catch (IOException e)
            {
                // store the exception
                LastException = e;
                // close the file
                _fileStream.Close();
                // and get out
                Stop();
                Console.WriteLine(e.ToString());
            }
		}
		
		public string FileName
		{
			get;
			protected set;
		}
        
        public Exception LastException
        {
            get;
            protected set;
        }
		
		protected IProducer<FMRShell.MarketData> _producer;
        bool _append;
        FileStream _fileStream;
        FMRShell.K300MaofTypeToString marketDataToString;
        StreamWriter _streamWriter;
	}
	
}