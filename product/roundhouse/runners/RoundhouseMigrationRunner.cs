using NHibernate.Cfg;

namespace roundhouse.runners
{
    using System;
    using System.Collections.Generic;
    using System.Data.SqlClient;
    using System.Diagnostics;
    using System.IO;

    using databases;
    using folders;
    using infrastructure;
    using infrastructure.app;
    using infrastructure.app.tokens;
    using infrastructure.extensions;
    using infrastructure.filesystem;
    using infrastructure.logging;
    using migrators;

    using NHibernate.Mapping;

    using resolvers;
    using Environment = environments.Environment;

    public class RoundhouseMigrationRunner : IRunner
    {
        protected string repository_path;
        protected Environment environment;
        protected KnownFolders known_folders;
        protected FileSystemAccess file_system;
        public DatabaseMigrator database_migrator { get; protected set; }
        protected VersionResolver version_resolver;
        public bool silent { get; set; }
        public bool dropping_the_database { get; set; }
        public bool dont_create_the_database;
        public bool run_in_a_transaction;
        protected ConfigurationPropertyHolder configuration;

        protected const string SQL_EXTENSION                  = "*.sql";
        protected const string INDIVIDUAL_SCRIPTS_FOLDER_NAME = "individualScripts";

        private const int LINE_WIDTH = 72;

        protected const string LINE_MARKER_DURING_MIGRATION = "-- #### RHMigration: Scripts to be run during migration ################ --";
        protected const string LINE_MARKER_AFTER_MIGRATION  = "-- #### RHMigration: Scripts to be run after migration ################# --";
        protected const string COMBINED_SCRIPT_FILE_NAME = "combinedScripts.sql";

        private readonly object LOCK_COMBINED_SCRIPT        = new object();
        private readonly object LOCK_SQL_FILE               = new object();

        private static int simple_output_file_number_before_up = 100;
        private static int simple_output_file_number_normal    = 200;
        private static int simple_output_file_number_after_up  = 800;

        public RoundhouseMigrationRunner(
            string repository_path,
            Environment environment,
            KnownFolders known_folders,
            FileSystemAccess file_system,
            DatabaseMigrator database_migrator,
            VersionResolver version_resolver,
            bool silent,
            bool dropping_the_database,
            bool dont_create_the_database,
            bool run_in_a_transaction,
            bool use_simple_recovery,
            ConfigurationPropertyHolder configuration)
        {
            this.known_folders = known_folders;
            this.repository_path = repository_path;
            this.environment = environment;
            this.file_system = file_system;
            this.database_migrator = database_migrator;
            this.version_resolver = version_resolver;
            this.silent = silent;
            this.dropping_the_database = dropping_the_database;
            this.dont_create_the_database = dont_create_the_database;
            this.run_in_a_transaction = run_in_a_transaction;
            this.configuration = configuration;
            database_migrator.create_script_handler += database_create_script_handler;
        }

        void database_create_script_handler(object sender, CreateScriptEventArgs e)
        {
            put_script_in_change_drop_for_database_command(e.FileName, e.SqlScript, e.BeforeUpScript);
        }

        public void run()
        {
            initialize_database_connections();

            log_initial_events();

            if (configuration.DryRun)
            {
                this.log_info_event_on_bound_logger("This is a dry run, nothing will be done to the database.");
                //WaitForKeypress();
            }

            handle_invalid_transaction_argument();
            create_change_drop_folder_and_log();

            try
            {
                this.log_action_starting();

                if (configuration.SimpleOutput)
                {
                    create_default_combined_script_path_file_if_needed(get_combined_script_file_path());
                }

                create_share_and_set_permissions_for_change_drop_folder();

                var hasRoundhouseSupportTables = database_migrator.database.has_roundhouse_support_tables();
                if (configuration.DryRun && hasRoundhouseSupportTables == false)
                {
                    log_warning_event_on_bound_logger("Database {1} on server {2} does not have the RoundhousE support{0}" +
                        "tables (ScriptsRun, ScriptsRunErrors, Version) created.{0}{0}" +
                        "You can not run RoundhousE in dry run mode against this database. To fix this: {0}" +
                        "    (A) -EITHER- Run RoundhousE in regular mode (without the --dryrun switch){0}" +
                        "    (B) -OR- Run the '{3}' script located in the drop folder manually.{0}{0}",
                        System.Environment.NewLine,
                        database_migrator.database.database_name,
                        database_migrator.database.server_name,
                        "CreateRoundhouseSupportTables.sql");
                    var script = database_migrator.database.generate_support_tables_script();
                    put_script_in_change_drop_for_database_command("CreateRoundhouseSupportTables", script, true);
                    return;
                }

                // TODO (PMO): This was commented out, figure out if we can turn it on again?
                //database_migrator.backup_database_if_it_exists();
                remove_share_from_change_drop_folder();

                bool database_was_created = false;

                if (!dropping_the_database)
                {
                    if (!dont_create_the_database)
                    {
                        database_was_created = create_or_restore_the_database();
                    }
                    
                    set_database_recovery_mode();

                    open_connection_in_transaction_if_needed();

                    log_and_run_support_tasks();

                    string new_version = version_resolver.resolve_version();
                    long version_id = 0;
                    try
                    {
                        version_id = log_and_run_version_the_database(new_version);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // this is where we bug out if in dry run and no support tables
                        return;
                    }

                    run_out_side_of_transaction_folder(known_folders.before_migration, version_id, new_version);
                    
                    log_migration_scripts();
                    log_and_traverse_known_folders(version_id, new_version, database_was_created);

                    if (run_in_a_transaction)
                    {
                        if (configuration.DryRun)
                        {
                            this.log_info_event_on_bound_logger("{0}-DryRun-Would have committed the transaction on database {1}", 
                                System.Environment.NewLine,
                                database_migrator.database.database_name);                            
                        }
                        else
                        {
                            database_migrator.close_connection();
                            database_migrator.open_connection(false);
                        }
                    }
                    log_and_traverse(known_folders.permissions, version_id, new_version, ConnectionType.Default);
                    run_out_side_of_transaction_folder(known_folders.after_migration, version_id, new_version);

                    if (configuration.DryRun)
                    {
                        log_info_event_on_bound_logger(
                            "{0}{0}-DryRun-{1} v{2} would have kicked your database ({3})! You would be at version {4}. All changes and backups can be found at \"{5}\".",
                            System.Environment.NewLine,
                            ApplicationParameters.name,
                            VersionInformation.get_current_assembly_version(),
                            database_migrator.database.database_name,
                            new_version,
                            known_folders.change_drop.folder_full_path);
                    }
                    else
                    {
                        log_info_event_on_bound_logger(
                            "{0}{0}{1} v{2} has kicked your database ({3})! You are now at version {4}. All changes and backups can be found at \"{5}\".",
                            System.Environment.NewLine,
                            ApplicationParameters.name,
                            VersionInformation.get_current_assembly_version(),
                            database_migrator.database.database_name,
                            new_version,
                            known_folders.change_drop.folder_full_path);
                    }
                    database_migrator.close_connection();
                }
                else
                {
                    drop_the_database();
                }
            }
            catch (Exception ex)
            {
                this.log_exception_and_throw(ex);
            }
            finally
            {
                database_migrator.database.Dispose();
                //copy_log_file_to_change_drop_folder();

                if (configuration.ExploreChangeDrop)
                {
                    Process.Start(known_folders.change_drop.folder_full_path);
                }
            }
        }

        private void open_connection_in_transaction_if_needed()
        {
            if (configuration.DryRun && run_in_a_transaction)
            {
                log_info_event_on_bound_logger("{0}{0}-DryRun- Would have began a transaction on database {1}", 
                    System.Environment.NewLine,
                    database_migrator.database.database_name);
            }
            database_migrator.open_connection(run_in_a_transaction);
        }

        protected virtual void set_database_recovery_mode()
        {
            if (configuration.RecoveryMode != RecoveryMode.NoChange)
            {
                var script = database_migrator.set_recovery_mode(configuration.RecoveryMode == RecoveryMode.Simple);
                put_script_in_change_drop_for_database_command("SetRecoveryMode", script, true);
            }
        }

        protected virtual bool create_or_restore_the_database()
        {
            var retVal = database_migrator.create_or_restore_database(get_custom_create_database_script());
            var script = database_migrator.database.generate_create_database_script(get_custom_create_database_script());
            put_script_in_change_drop_for_database_command("CreateOrRestoreDatabase", script, true);
            return retVal;
        }

        protected virtual void log_info_event_on_bound_logger(string message, params object[] args)
        {
            get_bound_logger().log_an_info_event_containing(message, args);
        }

        protected virtual void WaitForKeypress()
        {
            Console.ReadLine();
        }

        protected virtual void initialize_database_connections()
        {
            this.database_migrator.initialize_connections();
        }

        protected virtual Logger get_bound_logger()
        {
            return Log.bound_to(this);
        }

        private void log_action_starting()
        {
            this.log_separation_line(true, true);
            log_info_event_on_bound_logger("Setup, Backup, Create/Restore/Drop");
            this.log_separation_line(true, false);
        }

        private void log_and_traverse_known_folders(long version_id, string new_version, bool database_was_created)
        {
            log_and_traverse_alter_database_scripts(version_id, new_version);
            log_and_traverse_after_create_database_scripts(database_was_created, version_id, new_version);
            log_and_traverse(known_folders.run_before_up, version_id, new_version, ConnectionType.Default);
            log_and_traverse(known_folders.up, version_id, new_version, ConnectionType.Default);
            log_and_traverse(known_folders.run_first_after_up, version_id, new_version, ConnectionType.Default);
            log_and_traverse(known_folders.functions, version_id, new_version, ConnectionType.Default);
            log_and_traverse(known_folders.views, version_id, new_version, ConnectionType.Default);
            log_and_traverse(known_folders.sprocs, version_id, new_version, ConnectionType.Default);
            log_and_traverse(known_folders.indexes, version_id, new_version, ConnectionType.Default);
            log_and_traverse(known_folders.run_after_other_any_time_scripts, version_id, new_version, ConnectionType.Default);
        }

        private void log_and_traverse_after_create_database_scripts(
            bool database_was_created,
            long version_id,
            string new_version)
        {
            if (database_was_created)
            {
                log_and_traverse(known_folders.run_after_create_database, version_id, new_version, ConnectionType.Default);
            }
        }

        protected virtual void log_and_traverse_alter_database_scripts(long version_id, string new_version)
        {
            database_migrator.open_admin_connection();
            log_and_traverse(known_folders.alter_database, version_id, new_version, ConnectionType.Admin);
            database_migrator.close_admin_connection();
        }

        private void log_migration_scripts()
        {
            log_separation_line(true, true);
            log_info_event_on_bound_logger("Migration Scripts");
            log_separation_line(true, false);
        }

        private long log_and_run_version_the_database(string new_version)
        {
            log_separation_line(true, true);
            log_info_event_on_bound_logger("Versioning");
            log_separation_line(true, false);
            var current_version = database_migrator.get_current_version(repository_path);
            if (configuration.DryRun)
            {
                log_info_event_on_bound_logger(
                        "-DryRun- Would have migrated database {0} from version {1} to {2}.",
                        database_migrator.database.database_name,
                        current_version,
                        new_version);
            }
            else
            {
                log_info_event_on_bound_logger(
                        " Migrating {0} from version {1} to {2}.",
                        database_migrator.database.database_name,
                        current_version,
                        new_version);
            }
            return database_migrator.version_the_database(repository_path, new_version);
        }

        private void log_and_run_support_tasks()
        {
            this.log_separation_line(true, true);
            log_info_event_on_bound_logger("RoundhousE Structure");
            this.log_separation_line(true, false);
            if (configuration.DryRun)
            {
                log_info_event_on_bound_logger("-DryRun-Would have run roundhouse support tasks on database {1}",
                    System.Environment.NewLine,
                    database_migrator.database.database_name
                    );
            }
            database_migrator.run_roundhouse_support_tasks();
            string script = "";
            try
            {
                script = database_migrator.database.generate_database_specific_script();
            }
            catch (SqlException ex)
            {
                if (configuration.DryRun)
                {
                    log_info_event_on_bound_logger("{0}{0}    Error: Would have run database specific tasks on database {1}{0}" + 
                        "           but I failed because:{0}" +
                        "           {2}{0}" +
                        "           This is probably because you are running RoundHouse in dry run mode against a database{0}" +
                        "           that hasn't had roundhouse run against it before--so the RoundhousE support tables{0}" +
                        "           don't exist yet. To fix, run RoundhousE without the --dryrun flag, or just execute the{0}" +
                        "           SQL below against the database:{0}{0}{3}",
                        System.Environment.NewLine,
                        database_migrator.database.database_name,
                        ex.Message,
                        database_migrator.database.generate_database_specific_script()
                        );
                }
            }
            put_script_in_change_drop_for_database_command("DatabaseSpecificTasks", script, true);
        }

        private void log_separation_line(bool isThick, bool leadingNewline)
        {
            if (leadingNewline)
            {
                log_info_event_on_bound_logger(System.Environment.NewLine);
            }

            if (isThick)
            {
                log_info_event_on_bound_logger("{0}", "=".PadRight(LINE_WIDTH, '='));
            }
            else
            {
                log_info_event_on_bound_logger("{0}", "-".PadRight(LINE_WIDTH, '-'));
            }
        }

        private void create_change_drop_folder_and_log()
        {
            this.create_change_drop_folder();
            log_debug_event_on_bound_logger("The change_drop (output) folder is: {0}", this.known_folders.change_drop.folder_full_path);
            log_debug_event_on_bound_logger("Using SearchAllSubdirectoriesInsteadOfTraverse execution: {0}",
                    this.configuration.SearchAllSubdirectoriesInsteadOfTraverse);
        }

        protected virtual void log_debug_event_on_bound_logger(string message, params object[] args)
        {
            this.get_bound_logger().log_a_debug_event_containing(message, args);
        }

        private void handle_invalid_transaction_argument()
        {
            if (this.run_in_a_transaction && !this.database_migrator.database.supports_ddl_transactions)
            {
                log_warning_event_on_bound_logger("You asked to run in a transaction, but this dabasetype doesn't support DDL transactions.");
                if (!this.silent)
                {
                    log_info_event_on_bound_logger("Please press enter to continue without transaction support...");
                    WaitForKeypress();
                }
                this.run_in_a_transaction = false;
            }
        }

        protected virtual void log_warning_event_on_bound_logger(string message, params object[] args)
        {
            get_bound_logger().log_a_warning_event_containing(message, args);
        }

        private void log_initial_events()
        {
            log_info_event_on_bound_logger("Running {0} v{1} against {2} - {3}.",
                    ApplicationParameters.name,
                    VersionInformation.get_current_assembly_version(),
                    this.database_migrator.database.server_name,
                    this.database_migrator.database.database_name);

            log_info_event_on_bound_logger("Looking in {0} for scripts to run.", this.known_folders.up.folder_path);

            if (!this.silent)
            {
                log_info_event_on_bound_logger("Please press enter when ready to kick...");
                WaitForKeypress();
            }
        }

        protected virtual void put_script_in_change_drop_for_database_command(string fileName, string sqlScript, bool beforeUpScript)
        {
            // create temp file from string
            var tempFileName = Path.GetTempFileName();
            File.WriteAllText(tempFileName, sqlScript);

            copy_to_change_drop_folder(
                tempFileName,
                fileName + ".sql",
                beforeUpScript ? known_folders.run_before_up : known_folders.run_after_other_any_time_scripts, 
                beforeUpScript ? ExecutionPhase.Before : ExecutionPhase.After
                );
        }

        protected virtual void drop_the_database()
        {
            if (configuration.DryRun)
            {
                log_info_event_on_bound_logger(
                    "{0}{0}-DryRun-{1} would have removed database ({2}). All changes and backups would be found at \"{3}\".",
                    System.Environment.NewLine,
                    ApplicationParameters.name,
                    database_migrator.database.database_name,
                    known_folders.change_drop.folder_full_path);
                var script = database_migrator.delete_database();
                put_script_in_change_drop_for_database_command("DropDatabase", script, true);
                database_migrator.close_connection();
            }
            else
            {
                database_migrator.open_admin_connection();
                database_migrator.delete_database();
                var script = database_migrator.delete_database();
                put_script_in_change_drop_for_database_command("DropDatabase", script, true);
                database_migrator.close_admin_connection();
                database_migrator.close_connection();
                log_info_event_on_bound_logger(
                    "{0}{0}{1} has removed database ({2}). All changes and backups can be found at \"{3}\".",
                    System.Environment.NewLine,
                    ApplicationParameters.name,
                    database_migrator.database.database_name,
                    known_folders.change_drop.folder_full_path);
            }
        }

        private void log_exception_and_throw(Exception ex)
        {
            this.get_bound_logger()
                .log_an_error_event_containing(
                    "{0} encountered an error.{1}{2}{3}",
                    ApplicationParameters.name,
                    this.run_in_a_transaction
                        ? " You were running in a transaction though, so the database should be in the state it was in prior to this piece running. This does not include a drop/create or any creation of a database, as those items can not run in a transaction."
                        : string.Empty,
                    System.Environment.NewLine,
                    ex.to_string());

            throw ex;
        }

        public void log_and_traverse(MigrationsFolder folder, long version_id, string new_version, ConnectionType connection_type)
        {
            log_separation_line(false, false);

            log_info_event_on_bound_logger("Looking for {0} scripts in \"{1}\"{2}{3}",
                                                            folder.friendly_name,
                                                            folder.folder_full_path,
                                                            folder.should_run_items_in_folder_once ? " (one-time only scripts)." : string.Empty,
                                                            folder.should_run_items_in_folder_every_time ? " (every time scripts)" : string.Empty);

            traverse_files_and_run_sql(folder.folder_full_path, version_id, folder, environment, new_version, connection_type);
        }

        public void run_out_side_of_transaction_folder(MigrationsFolder folder, long version_id, string new_version)
        {
            if (!string.IsNullOrEmpty(folder.folder_name))
            {
                if (run_in_a_transaction)
                {
                    database_migrator.close_connection();
                    database_migrator.open_connection(false);
                }

                log_and_traverse(folder, version_id, new_version, ConnectionType.Default);

                if (run_in_a_transaction)
                {
                    database_migrator.close_connection();
                    database_migrator.open_connection(run_in_a_transaction);
                }
            }
        }

        private string get_custom_create_database_script()
        {
            if (string.IsNullOrEmpty(configuration.CreateDatabaseCustomScript))
            {
                return configuration.CreateDatabaseCustomScript;
            }

            if (file_system.file_exists(configuration.CreateDatabaseCustomScript))
            {
                return file_system.read_file_text(configuration.CreateDatabaseCustomScript);
            }

            return configuration.CreateDatabaseCustomScript;
        }

        protected virtual void create_change_drop_folder()
        {
            file_system.create_directory(known_folders.change_drop.folder_full_path);
        }

        private void create_share_and_set_permissions_for_change_drop_folder()
        {
            if (!configuration.DryRun)
            {
                //todo: implement creating share with change permissions
                //todo: implement setting Everyone to full acess to this folder
            }
        }

        private void remove_share_from_change_drop_folder()
        {
            if (!configuration.DryRun)
            {
                //todo: implement removal of the file share
            }
        }

        //todo:down story

        public void traverse_files_and_run_sql(string directory, long version_id, 
            MigrationsFolder migration_folder, Environment migrating_environment,
            string repository_version, ConnectionType connection_type)
        {
            if (!does_directory_exist(directory)) return;

            var fileNames = configuration.SearchAllSubdirectoriesInsteadOfTraverse
                                ? get_the_names_of_files_in_directory_recursively(directory)
                                : get_the_names_of_all_files_in_directory_nonrecursively(directory);
            foreach (string sql_file in fileNames)
            {
                run_sql_and_copy_to_change_drop(version_id, migration_folder, migrating_environment, 
                    repository_version, connection_type, sql_file);
            }

            if (configuration.SearchAllSubdirectoriesInsteadOfTraverse)
            {
                return;
            }

            foreach (var child_directory in get_the_names_of_directories_in_directory(directory))
            {
                traverse_files_and_run_sql(child_directory, version_id, migration_folder, migrating_environment, repository_version, connection_type);
            }
        }

        protected virtual string[] get_the_names_of_directories_in_directory(string directory)
        {
            return file_system.get_all_directory_name_strings_in(directory);
        }

        protected virtual string[] get_the_names_of_all_files_in_directory_nonrecursively(string directory)
        {
            return file_system.get_all_file_name_strings_in(directory, SQL_EXTENSION);
        }

        protected virtual string[] get_the_names_of_files_in_directory_recursively(string directory)
        {
            return file_system.get_all_file_name_strings_recurevly_in(directory, SQL_EXTENSION);
        }

        protected virtual bool does_directory_exist(string directory)
        {
            return file_system.directory_exists(directory);
        }

        private void run_sql_and_copy_to_change_drop(
            long version_id,
            MigrationsFolder migration_folder,
            Environment migrating_environment,
            string repository_version,
            ConnectionType connection_type,
            string sql_file)
        {
            string sql_file_text = replace_tokens(get_file_text(sql_file));
            log_debug_event_on_bound_logger(" Found and running {0}.", sql_file);
            bool the_sql_ran = database_migrator.run_sql(
                sql_file_text,
                file_system.get_file_name_from(sql_file),
                migration_folder.should_run_items_in_folder_once,
                migration_folder.should_run_items_in_folder_every_time,
                version_id,
                migrating_environment,
                repository_version,
                repository_path,
                connection_type);
            if (the_sql_ran)
            {
                copy_to_change_drop_and_log_exception(migration_folder, sql_file);
            }
        }

        private void copy_to_change_drop_and_log_exception(MigrationsFolder migration_folder, string sql_file)
        {
            try
            {
                copy_to_change_drop_folder(sql_file, null, migration_folder, ExecutionPhase.During);
            }
            catch (Exception ex)
            {
                log_warning_event_on_bound_logger(
                    "Unable to copy {0} to {1}. {2}{3}",
                    sql_file,
                    migration_folder.folder_full_path,
                    System.Environment.NewLine,
                    ex.to_string());
            }
        }

        public virtual string get_file_text(string file_location)
        {
            return file_system.read_file_text(file_location);
        }

        private string replace_tokens(string sql_text)
        {
            if (configuration.DisableTokenReplacement)
            {
                return sql_text;
            }

            return TokenReplacer.replace_tokens(configuration, sql_text);
        }

        protected virtual void copy_to_change_drop_folder(string path_to_sql_file, string new_script_name, Folder migration_folder, ExecutionPhase executionPhase)
        {
            if (!configuration.DisableOutput)
            {
                string destination_file_path;
                if (configuration.SimpleOutput)
                {
                    var file_number = get_file_number_and_increment_relevant_counter(executionPhase);
                    var destination_file_name = get_destination_file_name(path_to_sql_file, new_script_name, true);
                    destination_file_path = file_system.combine_paths(
                        known_folders.change_drop.folder_full_path,
                        "scripts",
                        INDIVIDUAL_SCRIPTS_FOLDER_NAME,
                        String.Format("{0}_{1}", file_number, destination_file_name));
                    combine_script_into_one_file(path_to_sql_file, executionPhase);
                }
                else
                {
                    var destination_file_name = get_destination_file_name(path_to_sql_file, new_script_name, false);
                    destination_file_path = file_system.combine_paths(
                        known_folders.change_drop.folder_full_path,
                        "itemsRan",
                        migration_folder.folder_name,
                        destination_file_name.Replace(migration_folder.folder_path + "\\", string.Empty));
                }
                file_system.verify_or_create_directory(file_system.get_directory_name_from(destination_file_path));
                log_debug_event_on_bound_logger("Copying file {0} to {1}.", file_system.get_file_name_from(path_to_sql_file), destination_file_path);
                file_copy_unsafe(path_to_sql_file, destination_file_path);
            }
        }

        protected virtual void combine_script_into_one_file(string path_to_sql_file, ExecutionPhase phase)
        {
            var combinedScriptFilePath = get_combined_script_file_path();

            var insertAt = 0;
            var currentContents = new List<String>();
            lock (LOCK_COMBINED_SCRIPT)
            {
                currentContents = new List<string>(get_lines_from_file(combinedScriptFilePath));
            }

            if (phase == ExecutionPhase.Before)
            {
                for (var i = 0; i < currentContents.Count; i++)
                {
                    if (currentContents[i] == LINE_MARKER_DURING_MIGRATION)
                    {
                        insertAt = i;
                        break;
                    }
                }
            }
            else if (phase == ExecutionPhase.During)
            {
                for (var i = 0; i < currentContents.Count; i++)
                {
                    if (currentContents[i] == LINE_MARKER_AFTER_MIGRATION)
                    {
                        insertAt = i;
                        break;
                    }
                }
            }
            else
            {
                insertAt = currentContents.Count;
            }

            var contentsOfIndividualScript = new List<string>();
            lock (LOCK_SQL_FILE)
            {
                contentsOfIndividualScript = new List<string>(get_lines_from_file(path_to_sql_file));
                contentsOfIndividualScript.Insert(0, System.Environment.NewLine + "-- " + path_to_sql_file);
            }

            currentContents.InsertRange(insertAt, contentsOfIndividualScript);
            lock (LOCK_COMBINED_SCRIPT)
            {
                File.WriteAllLines(combinedScriptFilePath, currentContents.ToArray());  
            }
        }

        protected virtual string[] get_lines_from_file(string path_to_file)
        {
            return File.ReadAllLines(path_to_file);
        }

        protected virtual void create_default_combined_script_path_file_if_needed(string combinedScriptFilePath)
        {
            lock (LOCK_COMBINED_SCRIPT)
            {
                make_sure_folder_is_there(combinedScriptFilePath);
                // create initial file with tokens in it to make it easy to find insertion points
                File.WriteAllLines(combinedScriptFilePath, new string[] {System.Environment.NewLine, LINE_MARKER_DURING_MIGRATION, System.Environment.NewLine});
                File.AppendAllText(combinedScriptFilePath, System.Environment.NewLine + LINE_MARKER_AFTER_MIGRATION + System.Environment.NewLine);
            }
        }

        private void make_sure_folder_is_there(string combinedScriptFilePath)
        {
            var pathToFile = Path.GetFullPath(combinedScriptFilePath);
            var folderPath = Path.GetDirectoryName(pathToFile);
            file_system.verify_or_create_directory(folderPath);
        }

        public string get_combined_script_file_path()
        {
            string path = null;
            if (configuration.SimpleOutput)
            {
                path = file_system.combine_paths(known_folders.change_drop.folder_full_path, "scripts", COMBINED_SCRIPT_FILE_NAME);
            }

            return path;
        }

        public enum ExecutionPhase
        {
            Before,
            During,
            After
        }

        private int get_file_number_and_increment_relevant_counter(ExecutionPhase phase)
        {
            var retVal = 0;
            switch (phase)
            {
                case ExecutionPhase.Before:
                    retVal = simple_output_file_number_before_up++;
                    break;
                case ExecutionPhase.During:
                    retVal = simple_output_file_number_normal++;
                    break;
                case ExecutionPhase.After:
                    retVal = simple_output_file_number_after_up++;
                    break;
                default:
                    retVal = simple_output_file_number_normal++;
                    break;
            }

            return retVal;
        }

        private string get_destination_file_name(string path_to_sql_file, string new_script_name, bool strip_out_folder_names)
        {
            string destination_file_name;
            if (String.IsNullOrEmpty(new_script_name))
            {
                if (strip_out_folder_names)
                {
                    destination_file_name = file_system.get_file_name_from(path_to_sql_file);
                }
                else
                {
                    destination_file_name = path_to_sql_file;
                }
            }
            else
            {
                destination_file_name = new_script_name;
            }
            return destination_file_name;
        }

        protected virtual void file_copy_unsafe(string sql_file_ran, string destination_file)
        {
            file_system.file_copy_unsafe(sql_file_ran, destination_file, true);
        }
    }
}