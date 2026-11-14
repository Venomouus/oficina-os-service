Feature: Ciclo de vida da ordem de servico

  Scenario: OS aprovada segue para execucao e finaliza
    Given uma OS foi criada
    When o orcamento e criado
    And o pagamento e aprovado
    And a execucao e finalizada
    Then a OS deve estar com status "FINALIZADA"

  Scenario: pagamento recusado compensa o fluxo
    Given uma OS aguarda pagamento
    When o pagamento e recusado
    Then a OS deve estar com status "PAGAMENTO_RECUSADO"
