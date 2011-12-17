namespace littleserver
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System.Threading.Tasks;

    class Server
    {
        readonly UriTemplateTable dispatchTable = new UriTemplateTable();
        readonly HttpListener listener = new HttpListener();
        static readonly Action<UriTemplateMatch, HttpListenerContext> NotFoundHandler =
            new Action<UriTemplateMatch, HttpListenerContext>( OnNotFound );

        CancellationToken listenCanceller;
        readonly Scheduler scheduler;
        readonly TaskFactory factory;


        public Server( int port )
        {
            this.listener.Prefixes.Add( String.Format( "http://+:{0}/", port ) );
            this.dispatchTable.BaseAddress = new Uri( "http://localhost/" );
            this.dispatchTable.KeyValuePairs.Add(
                new KeyValuePair<UriTemplate, object>( new UriTemplate( "*" ), NotFoundHandler ) );

            this.scheduler = new Scheduler();
            this.factory = new TaskFactory( this.scheduler );
        }

        public void AddHandler( string uriTemplate, Action<UriTemplateMatch, HttpListenerContext> route )
        {
            this.dispatchTable.KeyValuePairs.Add(
                new KeyValuePair<UriTemplate, object>( new UriTemplate( uriTemplate, false ), route ) );
        }

        static void OnNotFound( UriTemplateMatch match, HttpListenerContext context )
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        Task<HttpListenerContext> StartGetContext()
        {
            return this.factory.FromAsync( this.listener.BeginGetContext, this.listener.EndGetContext );
        }

        public void Start()
        {
            this.scheduler.Start();
            
        }

        public void Stop()
        {
            this.listener.Stop();
        }

        void ListenerThread()
        {
            try
            {
                while ( true )
                {
                    HttpListenerContext context = this.listener.GetContext();
                    Console.WriteLine( "Handling {0}", context.Request.Url );
                    UriTemplateMatch match = this.dispatchTable.MatchSingle( context.Request.Url );

                    var handler = (Action<UriTemplateMatch, HttpListenerContext>)match.Data;
                    handler( match, context );
                }
            }
            catch ( Exception e )
            {
                Console.WriteLine( "Exception occurred: {0}", e );
            }
        }

        class Scheduler : TaskScheduler
        {
            bool active = false;
            readonly object lockObject = new object();
            readonly Queue<Task> scheduledTasks = new Queue<Task>();
            readonly Thread[] workerThreads = new Thread[Environment.ProcessorCount];

            public override int MaximumConcurrencyLevel
            {
                get { return this.workerThreads.Length; }
            }

            void CheckThreads()
            {
                for ( int i = 0; i < this.workerThreads.Length; i++ )
                {
                    if ( this.workerThreads[i] == null || !this.workerThreads[i].IsAlive )
                    {
                        this.workerThreads[i] = new Thread( WorkerThread );
                        this.workerThreads[i].IsBackground = true;
                        this.workerThreads[i].Name = "Scheduler Worker Thread " + i;
                        this.workerThreads[i].Start();
                    }
                }
            }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                // NOTE: Don't synchronize access here because this is only called by the debugger, and only when
                //       the rest of the process is paused.
                return this.scheduledTasks;
            }

            protected override void QueueTask( Task task )
            {
                lock ( this.lockObject )
                {
                    this.scheduledTasks.Enqueue( task );
                    Monitor.Pulse( this.lockObject );
                    CheckThreads();
                }
            }

            public void Start()
            {
                lock ( this.lockObject ) { this.active = true; }
            }

            public void Stop()
            {
                lock ( this.lockObject )
                {
                    this.active = false;
                    Monitor.PulseAll( this.lockObject );
                }
            }

            protected override bool TryExecuteTaskInline( Task task, bool taskWasPreviouslyQueued )
            {
                // Because we can't efficiently remove things from our queue, we don't bother; just let the dead
                // tasks accumulate. If we had a queue where we could remove from the middle, we'd do that instead.
                //
                // We also don't bother to enforce that the task is on one of our worker threads; whoever is using us
                // must know what they're doing.
                //
                return TryExecuteTask( task );
            }

            void WorkerThread()
            {
                while ( true )
                {
                    Task toExecute;
                    lock ( this.lockObject )
                    {
                        while ( this.active && this.scheduledTasks.Count == 0 )
                        {
                            Monitor.Wait( this.lockObject );
                        }
                        if ( !this.active ) { return; }
                        toExecute = this.scheduledTasks.Dequeue();
                    }

                    TryExecuteTask( toExecute ); // Don't really care about the return here.
                }
            }
        }
    }

    class Program
    {
        static void HandleFoo( UriTemplateMatch match, HttpListenerContext context )
        {
            var thingy = new JObject( new JProperty( "d", "foo" ) );

            string output = JsonConvert.SerializeObject( thingy );
            byte[] bytes = Encoding.UTF8.GetBytes( output );

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = bytes.Length;
            using ( Stream outputStream = context.Response.OutputStream )
            {
                outputStream.Write( bytes, 0, bytes.Length );
            }
            context.Response.Close();
        }

        static void Main( string[] args )
        {
            try
            {
                Server server = new Server( 8086 );
                server.AddHandler( "foo", HandleFoo );
                server.Start();
                Console.WriteLine( "Waiting..." );
                Console.ReadLine();
                server.Stop();
            }
            catch ( Exception e )
            {
                Console.WriteLine( "Error: {0}", e );
            }
        }
    }
}
