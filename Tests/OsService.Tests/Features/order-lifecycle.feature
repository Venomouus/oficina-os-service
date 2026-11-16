Feature: Ciclo de vida distribuido da ordem de servico
  A OS reage a eventos dos servicos de cobranca e execucao sem perder o historico.

  Scenario: Pagamento aprovado leva a execucao e finalizacao
    Given uma OS criada pela API
    When chega o evento "BUDGET_CREATED"
    And chega o evento "PAYMENT_APPROVED"
    And chega o evento "EXECUTION_FINISHED"
    Then a OS deve estar com status "FINALIZADA"
    And o historico deve ter 4 transicoes

  Scenario: Pagamento recusado compensa a OS
    Given uma OS criada pela API
    When chega o evento "BUDGET_CREATED"
    And chega o evento "PAYMENT_REJECTED"
    Then a OS deve estar com status "PAGAMENTO_RECUSADO"
    And o historico deve ter 3 transicoes

  Scenario: Falha de execucao compensa a OS paga
    Given uma OS criada pela API
    When chega o evento "BUDGET_CREATED"
    And chega o evento "PAYMENT_APPROVED"
    And chega o evento "EXECUTION_FAILED"
    Then a OS deve estar com status "EXECUCAO_COM_ERRO"
    And o historico deve ter 4 transicoes

  Scenario: Evento atrasado nao reabre OS finalizada
    Given uma OS criada pela API
    When chega o evento "BUDGET_CREATED"
    And chega o evento "PAYMENT_APPROVED"
    And chega o evento "EXECUTION_FINISHED"
    And chega o evento "PAYMENT_REJECTED"
    Then a OS deve estar com status "FINALIZADA"
    And o historico deve ter 4 transicoes
