﻿
using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Xml;
using System.Threading;

using System.Diagnostics;
using System.Reflection;
using System.ComponentModel;
using JQuant;

using System.Windows.Forms;

/// <summary>
/// depending on the compilation flags i am going to use
/// simulation or real TaskBar namespace
/// the rest of the application is using FMRShell and is not aware if 
/// this is a simulation or a real server connected
/// Actually this is not precise - Command Line interface contains some internal test commands
/// which require simulation engines
/// </summary>
#if USEFMRSIM
using TaskBarLibSim;
#else
using TaskBarLib;
#endif

/// <summary>
/// thin shell around either TaskBarLib 
/// or around FMRSym if compiler directive USEFMRSIM is defined 
/// </summary>
namespace FMRShell
{

    #region Connection

    public enum ConnectionState
    {
        [Description("Idle")]
        Idle,                              // never opened yet
        [Description("Established")]
        Established,
        [Description("Trying")]
        Trying,
        [Description("Closed")]
        Closed,
        [Description("Disposed")]
        Disposed                           // Dispose() called
    }


    /// <summary>
    /// this guy logins to the remote server
    /// and eventually will run state machine keeping the connection alive and kicking 
    /// and will notify other subsystems if and when the connection goes down
    /// this little guy is one of the most important. there are two sources of information
    /// related to the connection status
    /// - periodic attempts to read data stream through TaskBarLib.K300Class
    /// - TaskBarLib.UserClass 
    /// This class handles all included in the TasBarkLib.UserClass and login related
    /// When there is no real TaskBarLib the class calls TasBarkLibSim
    /// 
    /// Normally application will do something like
    /// FMRShell.Connection connection = new FMRShell.Connection("xmlfilename")
    /// bool openResult = connection.Open(errCode)
    /// do work with connection.userClass  of type TaskBarLib.UserClass
    /// connection.Dispose();
    /// </summary>
    public class Connection : IDisposable
    {
        /// <summary>
        /// use default hard coded user name and password
        /// </summary>
        public Connection()
        {
            //  set defaults
            _parameters = new ConnectionParameters(
                "aryeh",    // user name
                "abc123",   // password
                "12345"     // account
                );
            Init();
        }

        /// <summary>
        /// create connection using provided by application connection parameters
        /// </summary>
        /// <param name="parameters">
        /// A <see cref="ConnectionParameters"/>
        /// </param>
        public Connection(ConnectionParameters parameters)
        {
            _parameters = parameters;
            Init();
        }

        /// <summary>
        /// open connection based on the login information stored in the specified XML file
        /// See example of the XML file in the ConnectionParameters.xml
        /// </summary>
        /// <param name="filename">
        /// A <see cref="System.String"/>
        /// Name of the XML file where the user login credentials can be found
        /// </param>
        public Connection(string filename)
        {
            xmlFileName = filename;
            useXmlFile = true;
            Init();
        }

        /// <summary>
        /// do some general initialization common for all constructors
        /// </summary>
        private void Init()
        {
            state = ConnectionState.Idle;
            userClass = new UserClass();
        }

        /// <summary>
        /// application have to call this method to get rid of the 
        /// connection (close sockets, etc.)
        /// </summary>
        public void Dispose()
        {
            // call userClass.Logout
            userClass.Logout(sessionId);

            // set userClass to null
            state = ConnectionState.Disposed;
        }

        ~Connection()
        {
        }

        public int GetSessionId()
        {
            return sessionId;
        }

        public string GetErrorMsg()
        {
            return errMsg;
        }

        /// <summary>
        /// return false if the open connection fails 
        /// normally application will call Open() without arguments - blocking Open
        /// or Keep() - which runs a thread and attempts to keep the connection
        /// alive. 
        /// </summary>
        /// <param name="returnCode">
        /// A <see cref="System.Int32"/>
        /// </param>
        /// <returns>
        /// A <see cref="System.Boolean"/>
        /// True if Ok and returnCode is set to something meaningful
        /// Application will check that the method retruned true and only 
        /// after that analyze returnCode
        /// </returns>
        public bool Open(out int returnCode)
        {
            bool result = true;
            returnCode = 0;

            // should I read login credentials from XML file ?
            if (useXmlFile)
            {
                ReadConnectionParameters reader = new ReadConnectionParameters(xmlFileName);

                // let's try to read the file 
                result = reader.Parse(out _parameters);
            }

            if (result)
            {
                returnCode = userClass.Login(
                    _parameters.userName,
                    _parameters.userPassword,
                    _parameters.appPassword,
                    out  errMsg, out  sessionId);

                result = (returnCode >= 0);
                if (result)
                {
                    Console.WriteLine("SessionId is " + sessionId);
                    // returnCode == sessionId - check if not
                    if (sessionId != returnCode)
                    {
                        Console.WriteLine("Session Id=" + sessionId + ",returnCode=" + returnCode + " are not equal after Login");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// the calling thread will be blocked by this method until Login to the
        /// remote server succeeds 
        /// </summary>
        /// <param name="printProgress">
        /// A <see cref="System.Boolean"/>
        /// if set to true will print dots as login makes progress
        /// </param>
        /// <returns>
        /// A <see cref="System.Boolean"/>
        /// True if Ok and returnCode is set to something meaningful
        /// Application will check that the method retruned true and only 
        /// after that analyze returnCode
        /// 
        /// TODO: Using of IWrite is an ugly patch. All prints should be done in
        /// some dedicated delagate
        /// </returns>
        public bool Open(IWrite iWrite, out int returnCode, bool printProgress)
        {
            state = ConnectionState.Trying;
            int percent = 0;

            bool openResult = Open(out returnCode);

            if (printProgress)
            {
                //Print title + progress bar
                iWrite.WriteLine(Environment.NewLine);
                iWrite.WriteLine("0    10   20   30   40   50   60   70   80   90  100");
                iWrite.WriteLine("|----+----+----+----+----+----+----+----+----+----|");
            }


            string desc_ = "";

            // loop until login succeeds
            while (openResult)
            {
                int percent_1;
                string description;

                // I need a short delay here before next attempt - 5 seconds
                // but do it only when already in the process - this saves me time when 
                // I have connection already established - don't wait on the first attempt.
                if (percent > 0) Thread.Sleep(5 * 1000);

                userClass.GetLoginActivity(ref sessionId, out percent_1, out description);

                if (percent_1 != percent)
                {
                    if (printProgress)
                    {
                        //scale the dots to the progress bar
                        while (percent < percent_1)
                        {
                            iWrite.Write(".");
                            percent += 2;
                        }
                    }
                }

                if (desc_ != description)
                {
                    MessageBox.Show(description);
                    desc_ = description;
                }


                loginStatus = userClass.get_LoginState(ref sessionId);

                if (loginStatus == LoginStatus.LoginSessionActive)
                {
                    openResult = true;
                    break;
                }
                else if ((loginStatus == LoginStatus.LoginSessionInProgress))
                {
                    //do nothing - just continue the loop
                }
                else    //any other loginStatus indicates login error
                {
                    openResult = false;
                    break;
                }
            }

            return openResult;
        }


        public string GetUserName()
        {
            return _parameters.userName;
        }

        public LoginStatus loginStatus
        {
            get;
            protected set;
        }


        public ConnectionState state
        {
            get;
            protected set;
        }

        public string LoginErrorDesc()
        {
            return this.userClass.get_LoginErrorDesc(ref this.sessionId);
        }

        /// <value>
        /// in the real code TaskBarLib instead of TaskBarLibSim will be used
        /// </value>
        public UserClass userClass
        {
            get;
            protected set;
        }

        public ConnectionParameters Parameters
        {
            get
            {
                return _parameters;
            }
        }

        protected int sessionId;
        protected string errMsg;
        protected string xmlFileName;
        protected bool useXmlFile;
        protected ConnectionParameters _parameters;

    }//class Connection

    #endregion;

    #region Collect Market Data

    public enum DataType
    {
        Maof,
        Rezef,
        Madad,

        // keep this entry last
        Last
    }


    /// <summary>
    /// generic class 
    /// data container for the trading/market data - Maof options
    /// can hold fields like time stamp, bid/ask, last price, etc.
    /// this class is not going to be used directly but inherited
    /// </summary>
    public abstract class MarketData : ICloneable
    {
        public DateTime TimeStamp;
        public long Ticks;

        public abstract object Clone();
        
        public virtual string Values
        {
            get;
            protected set;
        }
        
        public virtual string Legend
        {
            get;
            protected set;
        }
    }

    /// <summary>
    /// Keeps an object of type K300RzfType, K300MaofType, K300MadadType
    /// </summary>
    public class MarketDataHolder : MarketData
    {
        protected const string delimiter = ",";

        /// <summary>
        /// Setup the data holder
        /// </summary>
        /// <param name="t">
        /// A <see cref="Type"/>
        /// K300RzfType, K300MaofType, K300MadadType
        /// </param>
        protected MarketDataHolder(Type t)
        {
            this.fields = t.GetFields();
        }
            
        public override object Clone()
        {
            Type t;

            // clone the data first
            t = this.data.GetType();
            object o = System.Activator.CreateInstance(t);

            {
                // help the code optimizser - local variables
                FieldInfo[] fields = this.fields;
                object data = this.data;
                
                // set all fields in the new data object
                foreach (FieldInfo fi in fields)
                {
                    fi.SetValue(o, fi.GetValue(data));
                }
            }

            // create object of the same type as myself
            t = this.GetType();
            MarketDataHolder mdh = (MarketDataHolder)System.Activator.CreateInstance(t);

            // setup fields in the new object
            mdh.Data = o;
            mdh.Ticks = this.Ticks;
            mdh.TimeStamp = this.TimeStamp;
            mdh.values = this.values;
            mdh.isInitialized = this.isInitialized;

            return mdh;
        }

        /// <summary>
        /// Sets property Values - string containig all values, for the future logging
        /// </summary>
        /// <param name="data">
        /// A <see cref="DataType"/>
        /// </param>
        protected void InitValues(object data)
        {
            StringBuilder sbData = new StringBuilder(fields.Length*10);

            object o = (object)data;
            this.data = o;
            
            foreach (FieldInfo field in fields)
            {
                object val = field.GetValue(o);
                sbData.Append(val.ToString());
                sbData.Append(delimiter);
            }

            
            sbData.Append(TimeStamp.ToString("hh:mm:ss.fff"));
            sbData.Append(",");
            sbData.Append(Ticks.ToString());
            
            this.values = sbData.ToString();

            isInitialized = true;
        }

        /// <value>
        /// keeps a string with values separated by the delimiter
        /// </value>
        public override string Values
        {
            get
            {
                if (!isInitialized)
                {
                    InitValues(this.Data);
                }
                return this.values;
            }
            protected set
            {
                this.values = value;
            }
        }
        
        public override string Legend
        {
            get
            {
                if (legend == null)
                {
                    InitLegend();
                }
                return legend;
            }
            protected set
            {
                legend = value;
            }
        }

        /// <summary>
        /// Initialize field Legend - list of all fields in the data 
        /// </summary>
        protected void InitLegend()
        {
            if (legend != null)
            {
                return;
            }
            
            StringBuilder sbLegend = new StringBuilder(fields.Length*10);

            foreach (FieldInfo field in fields)
            {
                string name = field.Name;
                sbLegend.Append(name);
                sbLegend.Append(delimiter);
            }
            sbLegend.Append("TimeStamp,Ticks");
            
            legend = sbLegend.ToString();
        }

        public object Data
        {
            get
            {
                return this.data;
            }
            
            set
            {
                isInitialized = false;
                this.data = value;
            }
        }

        /// <summary>
        /// This field is true if field Values is set and up to date
        /// </summary>
        protected bool isInitialized;
        private string values;
        private static string legend;
        private object data;
        protected FieldInfo[] fields;
    }


    /// <summary>
    /// sealed can potentially improve performamce 
    /// </summary>
    public sealed class MarketDataMadad : MarketDataHolder
    {
        public MarketDataMadad()
            : base(typeof(K300MadadType))
        {
        }
    } // class MarketDataMadad


    public sealed class MarketDataRezef : MarketDataHolder
    {
        public MarketDataRezef()
            : base(typeof(K300RzfType))
        {
        }
    } // class MarketDataRezef


    public sealed class MarketDataMaof : MarketDataHolder
    {
        public MarketDataMaof()
            : base(typeof(K300MaofType))
        {
        }
    } //  class MarketDataMaof



    public class SH161TypeToString : StructToString<SH161Type>
    {
        public SH161TypeToString(string delimiter)
            : base(delimiter)
        {
        }
    }

    /// <summary>
    /// this class used by the RxDataValidator to let the application know that
    /// something wrong with the incoming data
    /// </summary>
    public class DataValidatorEvent
    {
        public MarketDataMaof sync
        {
            get;
            set;
        }
        public MarketDataMaof async
        {
            get;
            set;
        }
    }

    /// <summary>
    /// This is a producer (see IProducer) 
    /// Given Connection object will open data stream and notify registered 
    /// data consumers (IConsumer)
    /// The class operates simultaneously in asynchronous and synchronous fashion. 
    /// The class installs an event listener by calling K300Class.K300StartStream
    /// Additonally class spawns a thread which does polling of the remote server 
    /// in case notification does not work correctly and to ensure that the data is 
    /// consistent. 
    /// There is a tricky part. I want to make sure that the data which we get by 
    /// polling and via asynchronous API is the same. Collector uses for this purpose
    /// a dedicated consumer - thread which polls the servers and compares the received 
    /// data with the one sent to it by the collector
    /// </summary>
    public class Collector
    {
        public class DataProducer : ProducerBase<MarketData>
        {
            public DataProducer(string name, Type dataType)
            {
                Listeners = new List<JQuant.IConsumer<MarketData>>(5);
                countEvents = 0;
                Name = name;
                marketData = (MarketDataHolder)System.Activator.CreateInstance(dataType);
            }

            public override bool AddConsumer(JQuant.IConsumer<MarketData> consumer)
            {
                // Console.WriteLine("MadadListeners.Add(consumer)");
                lock (Listeners)
                {
                    Listeners.Add(consumer);
                }
                return true;
            }

            public override bool RemoveConsumer(JQuant.IConsumer<MarketData> consumer)
            {
                lock (Listeners)
                {
                    Listeners.Remove(consumer);
                }
                return true;
            }

            public override void GetEventCounters(out System.Collections.ArrayList names,
                                                 out System.Collections.ArrayList values)
            {
                names = new System.Collections.ArrayList(4);
                values = new System.Collections.ArrayList(4);

                names.Add("Events"); values.Add(GetEvents());
                names.Add("Sinks"); values.Add(GetSinks());
            }

            
            /// <summary>
            /// Called by TaskBarLib. This method calls registered listeners and gets out 
            /// The idea behind it to be as fast as possible
            /// this is the point where some basic processing can be done like filter obvious
            /// errors
            /// </summary>
            /// <param name="data">
            /// A <see cref="K300MaofType"/>
            /// </param>
            protected void OnEvent(object data)
            {
                // no memory allocation here - I am using allready created object marketData
                // DateTimePrecise.Now suggests memory allocation, but probably .NET handles this efficiently
                marketData.TimeStamp = JQuant.DateTimePrecise.Now;
                marketData.Ticks = DateTime.UtcNow.Ticks;
                marketData.Data = data;
                countEvents++;


                // consumer should not modify the data. consumer has two options:
                // 1) handle the data in the context of the Collector thead
                // 2) clone the data and and postopone the procesing (delegate to another thread)
                // consumer can not remove itself from the list in the context of Notify and will 
                // spawn a separate thread to do the trick if required 
                lock (Listeners)
                {
                    foreach (JQuant.IConsumer<MarketData> consumer in Listeners)
                    {
                        consumer.Notify(countEvents, marketData);
                    }
                }
            }


            protected void OnMadad(ref K300MadadType data)
            {
                // boxing from struct to object - memcpy
                OnEvent(data);
            }

            protected void OnMaof(ref K300MaofType data)
            {
                // boxing from struct to object - memcpy
                OnEvent(data);
            }
            
            protected void OnRezef(ref K300RzfType data)
            {
                // boxing from struct to object - memcpy
                OnEvent(data);
            }

            protected int GetSinks()
            {
                return Listeners.Count;
            }


            protected int GetEvents()
            {
                return countEvents;
            }
            
            protected int countEvents;
            protected MarketDataHolder marketData;
            protected List<JQuant.IConsumer<MarketData>> Listeners;
        }
        
        public class MadadProducer : DataProducer
        {
            public MadadProducer(K300EventsClass k3)
                : base("Madad", typeof(MarketDataMadad))
            {
                k3.OnMadad += new _IK300EventsEvents_OnMadadEventHandler(OnMadad);
            }
        } // class MadadProducer

        public class MaofProducer : DataProducer
        {
            public MaofProducer(K300EventsClass k3)
                : base("Maof", typeof(MarketDataMaof))
            {
                k3.OnMaof += new _IK300EventsEvents_OnMaofEventHandler(OnMaof);
            }
        } // class MaofProducer

        public class RezefProducer : DataProducer
        {
            public RezefProducer(K300EventsClass k3)
                : base("Rezef", typeof(MarketDataRezef))
            {
                k3.OnRezef += new _IK300EventsEvents_OnRezefEventHandler(OnRezef);
            }
        } // class RezefProducer

        public Collector(int sessionId)
        {
            // create a couple of TaskBarLib objects required for access to the data stream 
            if (k300Class == null)
            {
                k300Class = new K300Class();
                k300Class.K300SessionId = sessionId;
            }

            if (k300EventsClass == null)
                k300EventsClass = new K300EventsClass();

            //set the filters:
            k300EventsClass.EventsFilterBaseAsset = BaseAssetTypes.BaseAssetMaof;
            //k300EventsClass.EventsFilterBno=??? //here we set a single security, if specified
            k300EventsClass.EventsFilterMadad = 1; //I want to receive also madad changes - no way to filter specific madad here, get them all
            k300EventsClass.EventsFilterMaof = 1;
            k300EventsClass.EventsFilterMonth = MonthType.December;
            k300EventsClass.EventsFilterRezef = 1;
            k300EventsClass.EventsFilterStockKind = StockKind.StockKindMenaya;
            k300EventsClass.EventsFilterStockMadad = MadadTypes.TLV25;

            //initialize inner producers:
            maofProducer = new MaofProducer(k300EventsClass);
            rezefProducer = new RezefProducer(k300EventsClass);
            madadProducer = new MadadProducer(k300EventsClass);
        }

        public void Start(DataType dt)
        {
            switch (dt)
            {
                case DataType.Maof:
                    int tries = 0;
                    int rc = -1;
                    while (rc != 0 && tries < 5)
                    {
                        rc = k300Class.K300StartStream(K300StreamType.MaofStream);
                        Console.WriteLine("MaofStream Started, rc=" + rc);
                        tries++; ;
                        if (rc != 0) Thread.Sleep(5 * 1000);
                    }
                    break;
                case DataType.Rezef:
                    rc = k300Class.K300StartStream(K300StreamType.RezefStream);
                    break;
                case DataType.Madad:
                    rc = k300Class.K300StartStream(K300StreamType.IndexStream);
                    //OR - try this instead:
                    //rc = k300Class.K300StartStream(K300StreamType.MaofStream);
                    Console.WriteLine("IndexStream Started, rc=" + rc);
                    break;
                default:
                    break;      //do nothing
            }
        }

        public void Stop(DataType dt)
        {
            int rc;
            switch (dt)
            {
                case DataType.Maof:
                    rc = k300Class.K300StopStream(K300StreamType.MaofStream);
                    Console.WriteLine("MaofStream stopped, rc= " + rc);
                    break;

                case DataType.Rezef:
                    rc = k300Class.K300StopStream(K300StreamType.RezefStream);
                    Console.WriteLine("RezefStream stopped, rc= " + rc);
                    break;

                case DataType.Madad:
                    // It is still not clear which stream to start here, 
                    // because Madad data is supported either in the Maof stream
                    // or in a special Index stream - both do the job
                    //so chose one of the following:

                    // ** 1 ** - here you need to register yorself with the 
                    // index events with appropriate method 'OnMadad'
                    //k300Class.K300StopStream(K300StreamType.MaofStream);
                    //Console.WriteLine("MaofStream stopped, rc= " + rc);

                    // ** 2 **
                    // this one definitely does the job so far
                    rc = k300Class.K300StopStream(K300StreamType.IndexStream);
                    Console.WriteLine("IndexStream stopped, rc= " + rc);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// SH161 Data contains weights of securities in TASE indices
        /// I use this one to retrieve weights for TA25 Index
        /// Call this method only once - the weights are the same for the rest of the trading session.
        /// </summary>
        /// <param name="iWrite"></param>
        public void GetSH161Data(IWrite iWrite)
        {
            Array x = null;
            int rc = k300Class.GetSH161(ref x, MadadTypes.TLV25);
            iWrite.WriteLine(rc.ToString());
            iWrite.WriteLine(x.GetLength(0).ToString());
            if (rc > 0)
            {
                for (int i = 0; i < x.GetLength(0); i++)
                {
                    iWrite.WriteLine(SH161ToString((SH161Type)x.GetValue(i)));
                }
            }
        }

        /// <summary>
        /// Writes a SH161Type structure to a csv string
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public static string SH161ToString(SH161Type t)
        {
            string r = "";
            r += t.BNO + ",";
            r += t.BNO_NAME + ",";
            r += t.PRC + ",";
            r += t.HON_RASHUM + ",";
            r += t.PCNT + ",";
            r += t.MIN_NV + ",";
            r += t.BNO_IN_MDD + ",";
            r += t.PUBLIC_PRCNT + ",";
            r += t.NV_TZAFA;

            return r;
        }

        public MaofProducer maofProducer;
        public RezefProducer rezefProducer;
        public MadadProducer madadProducer;


        protected K300Class k300Class;
        protected K300EventsClass k300EventsClass;

        protected MarketDataMaof marketDataOnMaof;
        protected MarketDataRezef marketDataOnRezef;
        protected MarketDataMadad marketDataOnMadad;

    }   //class Collector


    /// <summary>
    /// this class will get the data from specified data producer and write the data to the 
    /// specified file.
    /// </summary>
    public class TradingDataLogger : AsyncLogger
    {
        public class DataConsumer: IConsumer<MarketData>
        {
            public DataConsumer(TradingDataLogger dataLogger, IProducer<MarketData> producer)
            {
                this.dataLogger = dataLogger;
                this.producer = producer;
                producer.AddConsumer(this);
            }

            public void Stop()
            {
                producer.RemoveConsumer(this);
            }

            public void Notify(int count, MarketData data)
            {
                dataLogger.stampLatest = DateTimePrecise.Now;
                MarketData dataClone = (MarketData)(data.Clone());
                dataLogger.AddEntry(dataClone);
            }

            // a pointer to the container class
            protected TradingDataLogger dataLogger;
            protected IProducer<MarketData> producer;
        }  // DataSink

        
        /// <summary>
        /// Create the ASCII logger
        /// </summary>
        /// <param name="name">
        /// A <see cref="System.String"/>
        /// Debug info - name of the logger
        /// </param>
        /// <param name="filename">
        /// A <see cref="System.String"/>
        /// File to read the data
        /// </param>
        /// <param name="append">
        /// A <see cref="System.Boolean"/>
        /// If "append" is true and file exists logger will append the data to the end of the file
        /// </param>
        /// <param name="collector">
        /// A <see cref="FMRShell.Collector"/>
        /// Where to take data from - register consumer
        /// </param>
        /// <param name="legend">
        /// A <see cref="System.String"/>
        /// what to write at start of the file (can be null). If append is true the argument will be ignored
        /// </param>
        public TradingDataLogger(string name, string filename, bool append, IProducer<MarketData> producer, string legend)
            : base(name)
        {
            FileName = filename;
            fileStream = default(FileStream);
            streamWriter = default(StreamWriter);
            this.producer = producer;
            this.append = append;
            timeStamped = false;
            stampLatest = default(System.DateTime);
            stampOldest = default(System.DateTime);
            this.legend = legend;
            Type = LogType.CSV;
            notStoped = false;

            // I estimate size of FMRShell.MarketData struct 50 bytes
            // AsyncLogger will drop the events after approx 500K of data in the queue
            QueueSize = (500 * 1024) / 50;
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
                    if (append) fileStream = new FileStream(FileName, FileMode.Append, FileAccess.Write, FileShare.Read);
                    else fileStream = new FileStream(FileName, FileMode.Create, FileAccess.Write, FileShare.Read);
                    streamWriter = new StreamWriter(fileStream);
                }
                catch (IOException e)
                {
                    // store the exception
                    LastException = e;
                    if (fileStream != default(FileStream))
                    {
                        fileStream.Close();
                        // help Garbage collector to clean up the system resources 
                        streamWriter = default(StreamWriter);
                        fileStream = default(FileStream);
                    }
                    // and get out
                    break;
                }

                // register myself in the data producer
                this.dataConsumer = new DataConsumer(this, producer);


                // write legend at the top of the file
                try
                {
                    if (legend != null)
                    {
                        streamWriter.WriteLine(legend);
                    }
                }
                catch (IOException e)
                {
                    // store the exception
                    LastException = e;
                    // close the file
                    fileStream.Close();
                    // help Garbage collector
                    streamWriter = default(StreamWriter);
                    fileStream = default(FileStream);
                    Console.WriteLine(e.ToString());
                    // and get out
                    break;
                }

                //strat write file
                base.Start();

                first = true;
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
            base.Stop();
            
            dataConsumer.Stop();

            if (fileStream != default(FileStream))
            {
                streamWriter.Flush();
                fileStream.Flush();
                // help Garbage collector
                streamWriter = default(StreamWriter);
                fileStream = default(FileStream);
                Console.WriteLine("Logger " + GetName() + " file "+FileName+" closed");
            }
        }

        public override void Dispose()
        {
            if (fileStream != default(FileStream))
            {
                streamWriter.Flush();
                fileStream.Flush();
                fileStream.Close();
            }
            base.Dispose();
        }

        /// <summary>
        /// this method is abstract in the parent class
        /// write data to the file. this method is called from a separate
        /// thread
        /// </summary>
        /// <param name="data">
        /// A <see cref="System.Object"/>
        /// </param>
        protected override void WriteData(object data)
        {
            // set Logger::stampOldest
            if (first)
            {
                first = false;
                stampOldest = DateTime.Now;
            }
            
            // I have to decide on format of the log - ASCII or binary 
            // should I write any system info like version the data/software ?
            // at this point only ASCII is supported, no system info
            // write all fields of K300MaofType (data.k3Maof) in one line
            // followed by EOL

            MarketData marketData = (MarketData)data;

            // write the string to the file
            try
            {
                streamWriter.WriteLine(marketData.Values);
                // i want to make Flush from time to time
                // the question is when ? or let the OS to manage the things ?
                // _streamWriter.Flush();
                lock (this)
                {
                    countLog++;
                }
            }
            catch (ObjectDisposedException e)
            {
                // store the exception
                LastException = e;
                Console.WriteLine(e.ToString());
            }
            catch (IOException e)
            {
                Console.WriteLine(e.ToString());
                // store the exception
                LastException = e;
                // and get out
                Stop();
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

        protected IProducer<MarketData> producer;    // data producer
        protected DataConsumer dataConsumer;         // where producer will put data 

        bool append;
        FileStream fileStream;
        string legend;
        StreamWriter streamWriter;
        bool first;
    }

    

    #endregion


    #region Connection Params;
    /// <summary>
    /// handle XML file containing connection parameters
    /// </summary>
    class ReadConnectionParameters : XmlTextReader
    {
        public ReadConnectionParameters(string filename)
            : base(filename)
        {
        }

        private enum xmlState
        {
            [Description("BEGIN")]
            BEGIN,
            [Description("PARAMS")]
            PARAMS,
            [Description("USERNAME")]
            USERNAME,
            [Description("PASSWORD")]
            PASSWORD,
            [Description("ACCOUNT")]
            ACCOUNT
        }

        public bool Parse(out ConnectionParameters parameters)
        {
            xmlState state = xmlState.BEGIN;
            string username = "";
            string password = "";
            string account = "";

            bool result = true;
            string val;
            parameters = null;

            while (base.Read())
            {
                switch (base.NodeType)
                {
                    case XmlNodeType.Element:
                        val = base.Name;
                        if ((val.Equals("connectionparameters")) && (state == xmlState.BEGIN))
                        {
                            state = xmlState.PARAMS;
                        }
                        else if ((val.Equals("username")) && (state == xmlState.PARAMS))
                        {
                            state = xmlState.USERNAME;
                        }
                        else if ((val.Equals("password")) && (state == xmlState.USERNAME))
                        {
                            state = xmlState.PASSWORD;
                        }
                        else if ((val.Equals("account")) && (state == xmlState.PASSWORD))
                        {
                            state = xmlState.ACCOUNT;
                        }
                        else
                        {
                            Console.WriteLine("Failed at element " + val + " in state " + JQuant.EnumUtils.GetDescription(state));
                            result = false;
                        }
                        break;

                    case XmlNodeType.Text:
                        val = base.Value;
                        if (state == xmlState.USERNAME)
                        {
                            username = val;
                        }
                        else if (state == xmlState.PASSWORD)
                        {
                            password = val;
                        }
                        else if (state == xmlState.ACCOUNT)
                        {
                            account = val;
                        }
                        else
                        {
                            Console.WriteLine("Failed at text " + val + " in state " + JQuant.EnumUtils.GetDescription(state));
                            result = false;
                        }
                        break;

                    case XmlNodeType.EndElement:
                        // I will not check that endelement Name is Ok 
                        val = base.Name;
                        if ((val.Equals("connectionparameters")) && (state == xmlState.ACCOUNT))
                        {
                        }
                        else if ((val.Equals("username")) && (state == xmlState.USERNAME))
                        {
                        }
                        else if ((val.Equals("password")) && (state == xmlState.PASSWORD))
                        {
                        }
                        else if ((val.Equals("account")) && (state == xmlState.ACCOUNT))
                        {
                        }
                        else
                        {
                            Console.WriteLine("Failed at EndElement " + val + " in state " + JQuant.EnumUtils.GetDescription(state));
                            result = false;
                        }
                        break;
                }

                // something is broken in the XML file
                if (result)
                {
                    parameters = new ConnectionParameters(username, password, account);
                }
                else
                {
                    break;
                }
            }


            return result;
        }

    }

    /// <summary>
    /// User login credentials, IP address and everything else required
    /// to establish and keep connection. This is just a data holder
    /// </summary>
    /// <returns>
    /// A <see cref="System.Int32"/>
    /// </returns>
    public class ConnectionParameters
    {
        //use only one constructor
        public ConnectionParameters(string name, string password, string account)
        {
            userName = name;
            userPassword = password;
            Account = account;

            //these two aren't actually used, but some TaskBar functions require them.
            //so we set them to default values
            appPassword = "";
            Branch = "000";
        }

        public string userName
        {
            get;
            protected set;
        }

        public string userPassword
        {
            get;
            protected set;
        }

        public string appPassword
        {
            get;
            protected set;
        }

        public string Account
        {
            get;
            protected set;
        }

        public string Branch
        {
            get;
            protected set;
        }
    }

    #endregion;

    #region Config AS400DateTime;

    /// <summary>
    /// Used to get latency and synchronize local machine vs. AS400
    /// </summary>
    /// 
    public class AS400Synch
    {
        /// <summary>
        /// Returns latency in case of successful call, -1 otherwise
        /// </summary>
        /// <returns>A <see cref="System.Int32"/></returns>
        public static int GetLatency()
        {
            ConfigClass cs = new ConfigClass();
            AS400DateTime dt;
            int ltncy;
            int ret = cs.GetAS400DateTime(out dt, out ltncy);
            if (ret == 0) return ltncy;
            else return ret;
        }

        /// <summary>
        /// this method is used by CLI and probably redundant
        /// </summary>
        public static DateTime GetAS400DateTime()
        {
            ConfigClass cs = new ConfigClass();
            AS400DateTime dt;
            int ltncy;

            // larytet - we have to do something with ret, for example if non-zero
            // we should return null
            // GetLatency() and DoPing() handle return code            
            int ret = cs.GetAS400DateTime(out dt, out ltncy);
            return ConvertToDateTime(dt);
        }

        /// <summary>
        /// Ping once
        /// </summary>
        /// <param name="dt">A <see cref="AS400DateTime"/></param>
        /// <param name="latency">A <see cref="System.Int32"/></param>
        /// <returns>A <see cref="System.Boolean"/> true in case of sucess, false otherwise</returns>
        public static bool Ping(out DateTime dt, out int latency)
        {
            ConfigClass cs = new ConfigClass();
            AS400DateTime AS400dt;
            int ret = cs.GetAS400DateTime(out AS400dt, out latency);
            dt = ConvertToDateTime(AS400dt);
            if (ret == 0) return true;
            else return false;
        }

        /// <summary>
        /// Converts AS400DateTime to the .net 
        /// </summary>
        /// <param name="dt"><see cref="TaskBar.AS400DateTime"/></param>
        /// <returns><see cref="System.DateTime"/></returns>
        public static DateTime ConvertToDateTime(AS400DateTime dt)
        {
            return new DateTime(dt.year, dt.Month, dt.day, dt.hour, dt.minute, dt.second, dt.ms);
        }

        public static string ToShortCSVString(DateTime dt, int latency)
        {
            return DateTime.Now.ToString("hh:mm:ss.fff") + ","
                + dt.ToString("hh:mm:ss.fff") + ","
                + latency.ToString();
        }
    }

    #endregion;

    #region Orders FSM;

    public enum FMROrderState
    {
        IDLE,
        INITIALIZED,
        SENT,
        WaitingFMR,
        WaitingTASE,
        PASSED,
        UpdatingCanceling,
        EXECUTED,
        CANCELED,
        FATAL,
    }

    public enum FMROrderEvent
    {
        InitOrder,
        Send,
        GetOrderId,
        GetInternalError,
        ApproveFMR,
        ApproveTASE,
        ApproveCancelTASE,
        Execution,
    }

    public class FMROrder : IMaofOrder
    {
        protected FMROrder()
        {
        }

        public System.DateTime Created
        {
            get;
            set;
        }

        public TransactionType TransType
        {
            get;
            set;
        }

        public OrderType OrdrType
        {
            get;
            set;
        }

        /// <summary>
        /// call this method to send events to the Order Processor 
        /// </summary>
        /// <param name="orderEvent">
        /// A <see cref="FMROrderEvent"/>
        /// </param>
        public void SendEvent(FMROrderEvent orderEvent)
        {
            // call Order Processor
            newEvent(this, orderEvent);
        }

        public delegate void NewEvent(FMROrder order, FMROrderEvent orderEvent);

        /// <value>
        /// set by Order Processor when the order is created 
        /// </value>
        public NewEvent newEvent
        {
            get;
            set;
        }
    }


    /// <summary>
    /// Represents a single trading directive. Essentially is a data container.
    /// It's kept inside a list in the FSMClass, along with other outstanding orders.
    /// FSMClass takes care of its porcessing, logging and reporting.
    /// </summary>
    public class MaofOrder : LimitOrderBase, IMaofOrder
    {

        /// <summary>
        /// call this method to send events to the Order Processor 
        /// </summary>
        /// <param name="orderEvent">
        /// A <see cref="FMROrderEvent"/>
        /// </param>
        public void SendEvent(FMROrderEvent orderEvent)
        {
            // call Order Processor
            newEvent(this, orderEvent);
        }

        public delegate void NewEvent(MaofOrder order, FMROrderEvent orderEvent);

        /// <value>
        /// set by Order Processor when the order is created 
        /// </value>
        public NewEvent newEvent
        {
            get;
            set;
        }

        //ImplementIMaofOrder:
        public DateTime Created
        {
            get;
            protected set;
        }

        public new TransactionType TransType
        {
            get;
            set;
        }

        public OrderType OrdrType
        {
            get;
            set;
        }

        /// <summary>
        /// FSM state 
        /// </summary>
        public FMROrderState State
        {
            get;
            protected set;
        }

        /// <summary>
        /// TaskBar object used for sending the trading directive to the API
        /// </summary>
        MaofOrderType maofOrderType;

        //various reference IDs

        /// <summary>
        /// TASE reference no. of approval
        /// </summary>
        public string Asmachta
        {
            get;
            protected set;
        }

        /// <summary>
        /// Exchange member's internal ref. no. of approval
        /// </summary>
        public string AsmachtaFMR
        {
            get;
            protected set;
        }

        /// <summary>
        /// this one is only for taking care of internal errors, 
        /// not needed if one of the Asmachtas obtained
        /// </summary>
        public int OrderId
        {
            get;
            protected set;
        }


        //Special variables required by FMR to treat internal errors:

        /// <summary>
        /// TaskBar type
        /// </summary>
        public OrdersErrorTypes ErrorType
        {
            get;
            protected set;
        }

        /// <summary>
        /// Placeholder for the internal error ref. no.
        /// </summary>
        public int ErrNo
        {
            get;
            protected set;
        }

        /// <summary>
        /// Placeholder for the string message which accompanies internal error arrival
        /// </summary>
        public string VbMsg
        {
            get;
            protected set;
        }

        /// <summary>
        /// placeholder for data used for resubmitting orders in case of internal error 
        /// effectively contains only corrected price or quantity (in case of ReEnter error)
        /// or "YES" or "NO" strings in case of Confirmation error
        /// </summary>
        public string ReEnteredValue
        {
            get;
            protected set;
        }


        //Constructor
        /// <summary>
        /// Default constructor - usual case is a LMT order, initialize an Order object ready to further processing.
        /// </summary>
        /// <param name="_conn"><see cref="FMRShell.Connection"/> currently active connection
        /// which keeps all the needed FMR data - username, password, account no.
        /// as well as UserClass instance used to communicate with the part of API responsible for 
        /// processing of orders</param>
        /// <param name="_transactionType"><see cref="JQuant.TransactionType"/>  - BUY or SELL</param>
        /// <param name="_option"><see cref="JQuant.Option"/></param>
        /// <param name="_quantity"><see cref="System.Int32"/> quantity of options to trade</param>
        /// <param name="_limitPrice"><see cref="System.Double"/></param>
        public MaofOrder(
            FMRShell.Connection _conn,
            TransactionType _transactionType,
            Option _option,
            int _quantity,
            double _limitPrice  //probably int will match better here, because TaskBar uses it as a string
            )
        {
            //create an instance of TaskBar order object:
            maofOrderType = new MaofOrderType();

            //and assign to it all the needed parameters:
            maofOrderType.Account = _conn.Parameters.Account;
            maofOrderType.Branch = _conn.Parameters.Branch;
            maofOrderType.operation = "N";  //always create a new order here, update ("U") or delete ("D") will be applied to an existing object
            maofOrderType.Option = _option.IdNum.ToString();
            maofOrderType.Sug_Pkuda = "LMT";
            if (_transactionType == TransactionType.BUY)
                maofOrderType.ammount = _quantity.ToString();
            else maofOrderType.ammount = "-" + _quantity.ToString();
            maofOrderType.price = _limitPrice.ToString();
        }

        //overloaded constructor that addresses order types other than default LMT
        //- either IOC or FOK (although LMT is supported by this one as well)
        //IOC or FOK orders aren't passed to the limit order book, so they can 
        //be used in pinging the market or for exploiting immediate arbitrage opportunities.
        public MaofOrder(
            FMRShell.Connection _conn,
            TransactionType _transactionType,
            OrderType _orderType,
            Option _option,
            int _quantity,
            double _limitPrice  //probably int will match better here, because TaskBar uses it as a string
            )
        {
            maofOrderType = new MaofOrderType();

            maofOrderType.Account = _conn.Parameters.Account;
            maofOrderType.Branch = _conn.Parameters.Branch;
            maofOrderType.operation = "N";  //always create a new order here, update ("U") or delete ("D") will be applied to an existing object
            maofOrderType.Option = _option.IdNum.ToString();
            maofOrderType.Sug_Pkuda = EnumUtils.GetDescription(_orderType);
            if (_transactionType == TransactionType.BUY)
                maofOrderType.ammount = _quantity.ToString();
            else maofOrderType.ammount = "-" + _quantity.ToString();
            maofOrderType.price = _limitPrice.ToString();
        }

    }//class MaofOrder




    /// <summary>
    /// implements Maof order FSM
    /// </summary>
    public class MaofOrderFSM : MailboxThread<object>, IOrderProcessor
    {
        public class OrderSink : IConsumer<LimitOrderParameters>
        {
            /// <summary>
            /// Points to the <see cref="JQuant.Algorithm"/> class
            /// </summary>
            Algorithm algo
            {
                get;
                set;
            }

            public LimitOrderParameters OrderParams
            {
                get;
                protected set;
            }

            //implement IConsumer.Notify:
            public void Notify(int count, LimitOrderParameters LmtParms)
            {
                OrderParams = LmtParms;
            }

            //TODO - add it to Algo object (producer)

        }

        //A placeholder for Order parameters - only one order a time is processed by the FSM
        public LimitOrderParameters OrderParams
        {
            get;
            protected set;
        }


        /*protected override void HandleMessage(LimitOrderParameters OrdParams)
        {
            this.OrderParams = OrdParams;   //keep the data received by mail from the Algo machine
        }*/


        //points to an active connection to the TaskBar
        Connection conn
        {
            get;
            set;
        }

        public MaofOrderFSM() :
            base("MaofOrderFSM", 100)
        {
        }

        public bool Create(LimitOrderParameters OrdParams, out IMaofOrder order)
        {
            if (MFOrdersList == null) MFOrdersList = new List<MaofOrder>(20);   //enable initial capacity of 20 orders
            MaofOrder maofOrder = null;
            maofOrder = new MaofOrder(conn, OrdParams.TransType, OrdParams.Opt, OrdParams.Quantity, OrdParams.Price);

            if (maofOrder != null)
            {
                // set callback - i want to get events from other tasks
                maofOrder.newEvent = NewEvent;
                maofOrder.SendEvent(FMROrderEvent.InitOrder);
            }

            MFOrdersList.Add(maofOrder);

            order = maofOrder;

            return (order != null);
        }


        #region FSM Matrix

        /// <summary>
        /// called by another thread when FMR has something to say about
        /// the order, for example, Ok or rejected
        /// </summary>
        void NewEvent(MaofOrder order, FMROrderEvent orderEvent)
        {

            switch (orderEvent)
            {
                case FMROrderEvent.InitOrder:
                    processInitOrder(order);
                    break;
                case FMROrderEvent.Send:
                    processSend(order);
                    break;
                case FMROrderEvent.GetOrderId:
                    processGetOrderId(order);
                    break;
                case FMROrderEvent.GetInternalError:
                    processGetInternalError(order);
                    break;
                case FMROrderEvent.ApproveFMR:
                    processApproveFMR(order);
                    break;
                case FMROrderEvent.ApproveTASE:
                    processApproveTASE(order);
                    break;
                case FMROrderEvent.ApproveCancelTASE:
                    processApproveCancelTASE(order);
                    break;
                case FMROrderEvent.Execution:
                    processExecution(order);
                    break;
                default:
                    break;
            }

        }

        //Only appropriate states count, other cases yield error
        void processInitOrder(MaofOrder order)
        {
            switch (order.State)
            {
                case FMROrderState.IDLE:
                    break;
                case FMROrderState.PASSED:
                    break;
                default:
                    break;
            }

        }

        void processSend(MaofOrder order)
        {
            switch (order.State)
            {
                case FMROrderState.INITIALIZED:
                    break;
                case FMROrderState.UpdatingCanceling:
                    break;
                default:
                    break;
            }
        }

        void processGetOrderId(MaofOrder order)
        {
            switch (order.State)
            {
                case FMROrderState.SENT:
                    break;
                default:
                    break;
            }
        }

        void processGetInternalError(MaofOrder order)
        {
            switch (order.State)
            {
                case FMROrderState.SENT:
                    break;
                default:
                    break;
            }
        }

        void processApproveFMR(MaofOrder order)
        {
            switch (order.State)
            {
                case FMROrderState.WaitingFMR:
                    break;
                default:
                    break;
            }
        }

        void processApproveTASE(MaofOrder order)
        {
            switch (order.State)
            {
                case FMROrderState.WaitingFMR:
                    break;
                case FMROrderState.WaitingTASE:
                    break;
                default:
                    break;
            }
        }

        void processApproveCancelTASE(MaofOrder order)
        {
            switch (order.State)
            {
                case FMROrderState.WaitingFMR:
                    break;
                case FMROrderState.WaitingTASE:
                    break;
                default:
                    break;
            }
        }

        void processExecution(MaofOrder order)
        {
            switch (order.State)
            {
                case FMROrderState.WaitingFMR:
                    break;
                case FMROrderState.WaitingTASE:
                    break;
                default:
                    break;
            }
        }

        #endregion;


        //Do the work

        void InitOrder_IDLE()
        {
        }

        // create a message

        // send message to the mailbox






        public bool Submit(IMaofOrder order)
        {
            return true;
        }

        public bool Cancel(IMaofOrder order)
        {
            return true;
        }




        /// <summary>
        /// A storage place where FSM keeps all the active orders.
        /// </summary>
        List<MaofOrder> MFOrdersList;

    }

    #endregion;


    #region FMRPing
    /// <summary>
    /// FSM which handles events Login, Logout, Timer and can be in states - Idle, LinkUp, LinkDown
    /// If timer expires in LinkDown state FSM produces audible signal (beep)
    /// 
    /// ------------- Usage -------------
    /// FMRPing fmrPing = FMRPing.GetInstance();
    /// fmrPing.Start();   // start the FSM
    /// fmrPing.SendLogin(); // notify the FSM that ping should work now
    /// </summary>
    public class FMRPing : MailboxThread<FMRPing.Events>, IDisposable
    {

        public static FMRPing GetInstance()
        {
            if (instance == null)
            {
                instance = new FMRPing();
            }
            return instance;
        }

        public void SendLogin()
        {
            this.Send(Events.Login);
        }

        public void SendLogout()
        {
            this.Send(Events.Logout);
        }

        protected static FMRPing instance = null;
        protected enum State
        {
            Idle,
            LinkUp,
            LinkDown
        }

        public enum Events
        {
            // start the ping
            Login,

            // stop the ping
            Logout,

            // timer expired
            Timer,

            // send Ping
            PingTimer,

            // Ping returned
            PingOk,

            // Ping failed
            PingFailed
        }


        protected State state;

        /// <summary>
        /// use GetInstance() to get reference to the instance of the FMRPing 
        /// </summary>
        protected FMRPing()
            : base("FMRPing", 10)
        {
            int pingPeriod = 2;

            // i need a timer and a working thread
            timerTask = new TimerTask("FMRPngTmr");
            timers_5sec = new TimerList("FMRPng5", 5 * 1000, 2, this.TimerExpiredHandler, timerTask);
            timers_2sec = new TimerList("FMRPng2", pingPeriod * 1000, 2, this.PingTimerExpiredHandler, timerTask);
            timerTask.Start();

            Statistics2min = new IntStatistics("1 min", 1 * 60 / pingPeriod); // pings in 2 min
            Statistics10min = new IntStatistics("10 min", 10 * 60 / pingPeriod); // pings in 10 min
            Statistics1hour = new IntStatistics("1 hour", 1 * 60 * 60 / pingPeriod); // pings in 1 hour

            MaxMin2min = new IntMaxMin("1 min", 1 * 60 / pingPeriod); // pings in 2 min
            MaxMin10min = new IntMaxMin("10 min", 10 * 60 / pingPeriod); // pings in 10 min
            MaxMin1hour = new IntMaxMin("1 hour", 1 * 60 * 60 / pingPeriod); // pings in 1 hour

            state = State.Idle;
            jobQueue = CreateJobQueue();
        }


        public new void Dispose()
        {
            jobQueue.Dispose();
            timerTask.Dispose();
            base.Dispose();
            instance = null;
        }

        protected override void HandleMessage(Events taskEvent)
        {
            switch (state)
            {
                case State.Idle:
                    HandleIdle(taskEvent);
                    break;
                case State.LinkUp:
                    HandleLinkUp(taskEvent);
                    break;
                case State.LinkDown:
                    HandleLinkDown(taskEvent);
                    break;
            }
        }

        protected void HandleIdle(Events taskEvent)
        {
            switch (taskEvent)
            {
                case Events.Login:
                    countPings = 0;
                    configClass = new ConfigClass();
                    StartTimer();
                    StartPingTimer();
                    state = State.LinkUp; // assume link up state
                    break;
                case Events.Logout:
                    Console.WriteLine("FMRPing: logout in the idle state");
                    break;
                case Events.Timer:
                    // do not restart the timer
                    break;
                case Events.PingTimer:
                    // do not restart the timer
                    break;
                case Events.PingOk:
                    // ignore ping results 
                    break;
                case Events.PingFailed:
                    // ignore ping results 
                    break;
            }
        }

        protected void HandleLinkUp(Events taskEvent)
        {
            switch (taskEvent)
            {
                case Events.Login:
                    Console.WriteLine("FMRPing: Login in the linkup state");
                    break;
                case Events.Logout:
                    jobQueue.Stop();
                    jobQueue.Dispose();
                    jobQueue = CreateJobQueue();

                    state = State.Idle;
                    break;
                case Events.Timer:
                    if (countPings == 0)
                    {
                        Console.WriteLine("FMRPing: ping failed in linkup state, move to linkdown");
                        state = State.LinkDown;
                    }
                    countPings = 0;
                    // restart expired timer
                    StartTimer();
                    break;
                case Events.PingTimer:
                    SendPing();
                    StartPingTimer();
                    break;
                case Events.PingOk:
                    countPings++;
                    CountPingOk++;
                    break;
                case Events.PingFailed:
                    Console.WriteLine("FMRPing: ping failed in the linkup state");
                    CountPingFailed++;
                    break;
            }
        }

        protected void HandleLinkDown(Events taskEvent)
        {
            switch (taskEvent)
            {
                case Events.Login:
                    Console.WriteLine("FMRPing: Login in the linkdown state");
                    break;
                case Events.Logout:
                    jobQueue.Stop();
                    jobQueue.Dispose();
                    jobQueue = CreateJobQueue();

                    state = State.Idle;
                    break;
                case Events.Timer:
                    if (countPings != 0)
                    {
                        Console.WriteLine("FMRPing: move to linkup state");
                        state = State.LinkUp;
                    }
                    countPings = 0;
                    // restart expired timer
                    StartTimer();
                    break;
                case Events.PingTimer:
                    SendPing();
                    StartPingTimer();
                    DoBeep();
                    break;
                case Events.PingOk:
                    Console.WriteLine("FMRPing: ping Ok, move to linkup state");
                    state = State.LinkUp;
                    countPings++;
                    CountPingOk++;
                    break;
                case Events.PingFailed:
                    // nothing new here
                    CountPingFailed++;
                    break;
            }
        }

        /// <summary>
        /// send timer expired to the FSM 
        /// </summary>
        protected void TimerExpiredHandler(ITimer timer)
        {
            this.Send(Events.Timer);
        }

        protected void PingTimerExpiredHandler(ITimer timer)
        {
            this.Send(Events.PingTimer);
        }

        protected void DoBeep()
        {
            Console.Beep();
        }

        /// <summary>
        /// delegate called by working thread 
        /// </summary>
        protected void DoPing(ref object o)
        {
            int latency;
            AS400DateTime AS400dt;
            int ret = configClass.GetAS400DateTime(out AS400dt, out latency);
            bool b = (ret == 0);

            // update statistics
            if (b)
            {
                Statistics2min.Add(latency);
                Statistics10min.Add(latency);
                Statistics1hour.Add(latency);

                MaxMin2min.Add(latency);
                MaxMin10min.Add(latency);
                MaxMin1hour.Add(latency);
            }

            o = b;
        }

        /// <summary>
        /// delegate called by working thread 
        /// </summary>
        protected void PingDone(object o)
        {
            if ((bool)o)
            {
                this.Send(Events.PingOk);
            }
            else
            {
                this.Send(Events.PingFailed);
            }
        }

        protected void SendPing()
        {
            jobQueue.AddJob(DoPing, PingDone, null);
        }

        protected void StartTimer()
        {
            timers_5sec.Start();
        }

        protected void StartPingTimer()
        {
            timers_2sec.Start();
        }

        public IntStatistics Statistics2min
        {
            get;
            protected set;
        }

        public IntStatistics Statistics10min
        {
            get;
            protected set;
        }

        public IntStatistics Statistics1hour
        {
            get;
            protected set;
        }

        public IntMaxMin MaxMin2min
        {
            get;
            protected set;
        }

        public IntMaxMin MaxMin10min
        {
            get;
            protected set;
        }

        public IntMaxMin MaxMin1hour
        {
            get;
            protected set;
        }


        public int CountPingFailed
        {
            get;
            protected set;
        }

        public int CountPingOk
        {
            get;
            protected set;
        }

        protected static JQuant.JobQueue CreateJobQueue()
        {
            JQuant.JobQueue jobQueue = new JQuant.JobQueue("FMRPngJQ", 3);
            jobQueue.Start();

            return jobQueue;
        }

        protected ConfigClass configClass;
        protected TimerTask timerTask;
        protected TimerList timers_5sec;
        protected TimerList timers_2sec;
        protected int countPings;
        JQuant.JobQueue jobQueue;
    }// Class FmrPing
    #endregion
}//namespace
