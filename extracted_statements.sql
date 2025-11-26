-- ========================================
-- Extracted SQL Statements from Codebase
-- Total Statements: 2
-- ========================================

-- Statement ID: view_1
-- File: src/DocumentProcessor.Infrastructure/InfrastructureServiceCollectionExtensions.cs
-- Line: 143
-- Type: CREATE VIEW
-- SQL Server Specific Functions: DATEDIFF, GETUTCDATE, COALESCE
-- ========================================
CREATE VIEW vw_DocumentSummary AS
SELECT
    documenttypename,
    status,
    COUNT(*) AS DocumentCount,
    AVG(DATEDIFF(SECOND, uploadedat, COALESCE(processedat, GETUTCDATE()))) AS AvgProcessingTimeSeconds,
    MIN(uploadedat) AS FirstUploadedAt,
    MAX(uploadedat) AS LastUploadedAt
FROM dps_dbo.documents
WHERE isdeleted = 0
GROUP BY documenttypename, status;

-- ========================================
-- Statement ID: procedure_1
-- File: src/DocumentProcessor.Infrastructure/InfrastructureServiceCollectionExtensions.cs
-- Line: 160
-- Type: CREATE PROCEDURE
-- SQL Server Specific Syntax: CREATE PROCEDURE, @parameters, SET NOCOUNT ON, DATEADD, GETUTCDATE, NVARCHAR
-- ========================================
CREATE PROCEDURE sp_GetRecentDocuments
    @Days INT = 7,
    @Status INT = NULL,
    @DocumentTypeName NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        id,
        filename,
        fileextension,
        storagepath,
        filesize,
        documenttypename,
        documenttypecategory,
        status,
        processingstatus,
        summary,
        uploadedat,
        processedat,
        processingstartedat,
        processingcompletedat
    FROM dps_dbo.documents
    WHERE isdeleted = 0
        AND uploadedat >= DATEADD(DAY, -@Days, GETUTCDATE())
        AND (@Status IS NULL OR status = @Status)
        AND (@DocumentTypeName IS NULL OR documenttypename = @DocumentTypeName)
    ORDER BY uploadedat DESC
END;
