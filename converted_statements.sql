-- ========================================
-- Converted PostgreSQL Statements
-- Total Statements: 2
-- ========================================

-- Statement ID: view_1
-- Conversion Method: DMS_TOOL
-- File: src/DocumentProcessor.Infrastructure/InfrastructureServiceCollectionExtensions.cs
-- Line: 143
-- Type: CREATE VIEW
-- ========================================
CREATE VIEW dps_dbo.vw_documentsummary
AS
SELECT
    documenttypename, status, COUNT(*) AS documentcount, AVG(aws_sqlserver_ext.datediff('second', uploadedat::TIMESTAMP, COALESCE(processedat, timezone('UTC', CURRENT_TIMESTAMP(6)))::TIMESTAMP)) AS avgprocessingtimeseconds, MIN(uploadedat) AS firstuploadedat, MAX(uploadedat) AS lastuploadedat
    FROM dps_dbo.documents
    WHERE isdeleted = 0
    GROUP BY documenttypename, status;

-- ========================================
-- Statement ID: procedure_1
-- Conversion Method: MANUAL_AFTER_DMS_FAILURE
-- File: src/DocumentProcessor.Infrastructure/InfrastructureServiceCollectionExtensions.cs
-- Line: 160
-- Type: CREATE FUNCTION (converted from CREATE PROCEDURE)
-- Note: DMS tool failed with error: "Statement definition is not valid."
-- Manual conversion applied to transform SQL Server procedure to PostgreSQL function
-- ========================================
CREATE OR REPLACE FUNCTION dps_dbo.sp_getrecentdocuments(
    days INTEGER DEFAULT 7,
    status_param INTEGER DEFAULT NULL,
    documenttypename_param VARCHAR(200) DEFAULT NULL
)
RETURNS TABLE (
    id UUID,
    filename VARCHAR(500),
    fileextension VARCHAR(50),
    storagepath VARCHAR(1000),
    filesize BIGINT,
    documenttypename VARCHAR(200),
    documenttypecategory VARCHAR(100),
    status INTEGER,
    processingstatus INTEGER,
    summary TEXT,
    uploadedat TIMESTAMP,
    processedat TIMESTAMP,
    processingstartedat TIMESTAMP,
    processingcompletedat TIMESTAMP
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        d.id,
        d.filename,
        d.fileextension,
        d.storagepath,
        d.filesize,
        d.documenttypename,
        d.documenttypecategory,
        d.status,
        d.processingstatus,
        d.summary,
        d.uploadedat,
        d.processedat,
        d.processingstartedat,
        d.processingcompletedat
    FROM dps_dbo.documents d
    WHERE d.isdeleted = 0
        AND d.uploadedat >= (timezone('UTC', CURRENT_TIMESTAMP) - make_interval(days => days))
        AND (status_param IS NULL OR d.status = status_param)
        AND (documenttypename_param IS NULL OR d.documenttypename = documenttypename_param)
    ORDER BY d.uploadedat DESC;
END;
$$;
